using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application;
using JetBrains.Metadata.Reader.Impl;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.Util;
using Newtonsoft.Json.Linq;
using ReSharperMcp.Disk;

namespace ReSharperMcp.Tools
{
    public class GetFileErrorsTool : IMcpTool, IMcpExpandableTool
    {
        private const int DefaultMaxDiagnostics = 50;

        private static readonly HashSet<string> SourceExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb" };

        // Members the compiler binds by pattern rather than by name at a call site. When one does not resolve, the
        // usual cause is a source generator whose output Rider has not seen, not a broken file.
        private static readonly HashSet<string> PatternMembers = new HashSet<string>
        {
            "Deconstruct",
            "GetEnumerator",
            "GetAsyncEnumerator",
            "MoveNext",
            "Current",
            "GetAwaiter",
            "GetResult",
            "Dispose",
            "Add",
            "Slice",
        };

        private readonly ISolution _solution;

        public GetFileErrorsTool(ISolution solution) => _solution = solution;

        public string Name => "get_file_errors";

        public string Description =>
            "Fast compile check: syntax errors and unresolved references in a file, from the PSI tree. " +
            "Each unresolved reference is reported once, at its outermost unresolved qualifier; errors come first, " +
            "capped by 'maxDiagnostics'. Pass 'filePaths' to check several files, or 'changedSince' (a git ref) / " +
            "'changed: true' to check every changed source file in one call (errors only).";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Path to the file to check, absolute or relative to the solution root"
                },
                filePaths = new
                {
                    type = "array",
                    description = "Paths to check in batch. Results are concatenated with separators. Alternative to single 'filePath' parameter.",
                    items = new { type = "string" }
                },
                changedSince = new
                {
                    type = "string",
                    description = "Check every source file changed since this git ref (committed or not) plus untracked ones, e.g. 'HEAD', 'origin/main'. Reports only files with errors."
                },
                changed = new
                {
                    type = "boolean",
                    description = "Shorthand for changedSince: 'HEAD' — every modified or untracked source file."
                },
                maxDiagnostics = new
                {
                    type = "integer",
                    description = $"Maximum diagnostics to list per call (errors first). The total is always printed. Default: {DefaultMaxDiagnostics}."
                }
            },
            required = new string[0]
        };

        public JObject Expand(JObject arguments)
        {
            var reference = arguments["changedSince"]?.ToString();
            if (string.IsNullOrEmpty(reference) && arguments["changed"]?.Value<bool>() == true)
                reference = "HEAD";

            if (string.IsNullOrEmpty(reference))
                return arguments;

            var expanded = (JObject)arguments.DeepClone();
            expanded["changedScope"] = $"changed since {reference}";
            try
            {
                var directory = SolutionRoot.Of(_solution).Workspace
                    ?? throw new InvalidOperationException("the solution has no directory");
                var files = GitChanges.Since(directory, reference)
                    .Where(x => SourceExtensions.Contains(Path.GetExtension(x)))
                    .ToList();
                expanded["filePaths"] = new JArray(files);
            }
            catch (InvalidOperationException exception)
            {
                expanded["changedError"] = exception.Message;
            }

            return expanded;
        }

        public object Execute(JObject arguments)
        {
            if (arguments["changedScope"] != null)
                return ExecuteChanged(arguments);

            return ToolBatch.Of(arguments, "filePaths", item: "filePath")?.Run(ExecuteSingle) ?? ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            return Inspect(filePath).Render(filePath, MaxDiagnostics(arguments));
        }

        /// <summary>
        /// Errors only, in files that have any: the shape a post-edit or pre-commit hook wants.
        /// </summary>
        private object ExecuteChanged(JObject arguments)
        {
            var scope = arguments["changedScope"].ToString();
            if (arguments["changedError"] != null)
                return new { error = $"Cannot list files {scope}: {arguments["changedError"]}" };

            var changed = (arguments["filePaths"] as JArray)?.Select(x => x.ToString()).ToList() ?? new List<string>();
            if (changed.Count == 0)
                return $"{scope}: no changed source files";

            // Untracked scratch files and other projects' files are not this solution's to check.
            var files = changed.Where(IsInSolution).ToList();
            var outside = changed.Except(files).ToList();

            var reports = files.Select(file => (file, report: InspectIsolated(file))).ToList();
            var failing = reports.Where(x => x.report.Failed).ToList();
            var withErrors = failing.Count(x => x.report.Errors.Any());
            var errors = failing.Sum(x => x.report.Errors.Count());

            var text = new StringBuilder();
            text.Append($"{scope}: {files.Count} files checked, {withErrors} with errors, {errors} errors");
            if (failing.Count > withErrors)
                text.Append($", {failing.Count - withErrors} could not be checked");

            if (outside.Count > 0)
            {
                var listed = string.Join(", ", outside.Take(3).Select(Relative));
                var more = outside.Count > 3 ? $", +{outside.Count - 3} more" : "";
                text.AppendLine().Append($"skipped {outside.Count} changed files in no project of this solution ({listed}{more})");
            }

            var budget = MaxDiagnostics(arguments);
            foreach (var (file, report) in failing)
            {
                text.AppendLine().AppendLine();
                text.Append(report.RenderErrors(Relative(file), ref budget));
            }

            return text.ToString();
        }

        private FileReport InspectIsolated(string filePath)
        {
            try
            {
                return Inspect(filePath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interruption.Current.CheckAndThrow();
                return FileReport.Failure(ToolFailure.Logged(exception, Relative(filePath)));
            }
        }

        private FileReport Inspect(string filePath)
        {
            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return FileReport.Failure(resolved.Error);

            var sourceFile = resolved.SourceFile;
            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return FileReport.Failure("Could not get PSI tree for file");

            // Analysis below may reach ReSharper code that reads the ambient compilation context.
            using var context = CompilationContexts.For(sourceFile);
            var unloaded = UnloadedProject(sourceFile);
            if (unloaded != null)
                return FileReport.Failure(unloaded);

            var report = new FileReport();
            foreach (var node in psiFile.Descendants())
            {
                try
                {
                    Inspect(node, report);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // ReSharper can throw from deep inside its own analysis (observed: injected-PSI
                    // collection over a stale chameleon). One bad node must not lose the whole file.
                    Interruption.Current.CheckAndThrow();
                    report.InspectionFailed(exception);
                }
            }

            return report;
        }

        /// <summary>
        /// A project whose references are not loaded or restored resolves nothing, and listing every unresolved
        /// name in it buries the one useful fact (observed: 81k characters for one file).
        /// </summary>
        private static string UnloadedProject(IPsiSourceFile sourceFile)
        {
            var module = sourceFile.PsiModule;
            var objectType = module.GetPredefinedType().Object.GetTypeElement();
            var listType = TypeElementUtil.GetTypeElementByClrName(new ClrTypeName("System.Collections.Generic.List`1"), module);
            if (objectType != null && listType != null)
                return null;

            var project = sourceFile.GetProject()?.Name ?? module.DisplayName;

            return $"Project {project} is not loaded or restored in Rider (System.Object or List<T> does not resolve), "
                + "so diagnostics are unavailable. Reload or restore the project in Rider, or use dotnet build.";
        }

        private static void Inspect(ITreeNode node, FileReport report)
        {
            // Syntax errors.
            if (node is IErrorElement errorElement)
                report.Add(node, "error", errorElement.ErrorDescription);

            // A reference inside a string literal is an injected language (regex, HTML, ...), never a
            // compile error, and asking for it triggers ReSharper's injected-PSI scan of the whole file.
            if (PsiHelpers.IsLiteral(node))
                return;

            // Unresolved references. A '_' is a discard (`out _`, `(_, x)`), never a name to resolve.
            foreach (var reference in node.GetReferences())
            {
                if (reference.GetName() == "_")
                    continue;

                var errorType = reference.Resolve().ResolveErrorType;
                if (errorType == ResolveErrorType.OK || errorType == ResolveErrorType.IGNORABLE)
                    continue;

                var severity = errorType == ResolveErrorType.DYNAMIC ? "warning" : "error";
                var name = reference.GetName();
                var unverified = MayBeGenerated(node, name) ? " (unverified: may be source-generated)" : "";
                report.AddUnresolved(node, severity, $"Cannot resolve symbol '{name}'{unverified}");
            }
        }

        /// <summary>
        /// Whether an unresolved name could be a member a source generator adds: a pattern member, or a member of
        /// a partial type (the qualifier's, or the enclosing one for an unqualified name).
        /// </summary>
        private static bool MayBeGenerated(ITreeNode node, string name)
        {
            if (PatternMembers.Contains(name))
                return true;

            if (node is IReferenceExpression { QualifierExpression: { } qualifier })
                return IsPartial(qualifier.Type().GetTypeElement());

            return node is IReferenceExpression && IsPartial(node.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement);
        }

        private static bool IsPartial(ITypeElement type) =>
            type != null && type.GetDeclarations().OfType<IClassLikeDeclaration>().Any(x => x.IsPartial);

        private bool IsInSolution(string path) =>
            _solution.FindProjectItemsByLocation(VirtualFileSystemPath.Parse(path, InteractionContext.SolutionContext))
                .OfType<IProjectFile>()
                .Any();

        private string Relative(string path) =>
            SolutionRoot.Of(_solution).Relative(path);

        private static int MaxDiagnostics(JObject arguments)
        {
            var max = arguments["maxDiagnostics"]?.Value<int>() ?? DefaultMaxDiagnostics;

            return max > 0 ? max : DefaultMaxDiagnostics;
        }

        /// <summary>
        /// The diagnostics of one file, deduplicated as they arrive.
        /// </summary>
        private sealed class FileReport
        {
            private readonly List<DiagnosticEntry> _entries = new List<DiagnosticEntry>();
            private readonly Dictionary<(int, int), DiagnosticEntry> _unresolvedAt = new Dictionary<(int, int), DiagnosticEntry>();
            private readonly Dictionary<string, int> _inspectionFailures = new Dictionary<string, int>();
            private string _failure;

            public bool Failed => _failure != null || Errors.Any();

            public IEnumerable<DiagnosticEntry> Errors => _entries.Where(x => x.Severity == "error");

            public static FileReport Failure(string message) => new FileReport { _failure = message };

            public void Add(ITreeNode node, string severity, string message)
            {
                var entry = Entry(node, severity, message);
                if (entry != null && !_entries.Any(x => x.Line == entry.Line && x.Column == entry.Column && x.Message == entry.Message))
                    _entries.Add(entry);
            }

            /// <remarks>
            /// <c>journey.Departing</c> starts where <c>journey</c> does, and the walk visits the outer node first.
            /// The later, inner entry at the same position is the root cause, so it replaces the earlier one.
            /// </remarks>
            public void AddUnresolved(ITreeNode node, string severity, string message)
            {
                var entry = Entry(node, severity, message);
                if (entry == null)
                    return;

                if (_unresolvedAt.TryGetValue((entry.Line, entry.Column), out var outer))
                    _entries.Remove(outer);

                _unresolvedAt[(entry.Line, entry.Column)] = entry;
                _entries.Add(entry);
            }

            public void InspectionFailed(Exception exception)
            {
                var type = exception.GetType().Name;
                _inspectionFailures[type] = _inspectionFailures.TryGetValue(type, out var count) ? count + 1 : 1;
            }

            public string Render(string filePath, int max)
            {
                if (_failure != null)
                    return $"{filePath} — error: {_failure}";

                var ordered = Ordered();
                var text = new StringBuilder();
                text.Append(filePath).Append(" — ").Append(ordered.Count).Append(" diagnostics");
                if (ordered.Count > max)
                    text.Append($" (showing {max}; raise maxDiagnostics to see more)");
                text.AppendLine();

                foreach (var entry in ordered.Take(max))
                    entry.AppendTo(text);

                AppendPartial(text);

                return text.ToString().TrimEnd();
            }

            public string RenderErrors(string filePath, ref int budget)
            {
                if (_failure != null)
                    return $"=== {filePath} ===\nerror: {_failure}";

                var errors = Errors.OrderBy(x => x.Line).ThenBy(x => x.Column).ToList();
                var text = new StringBuilder($"=== {filePath} — {errors.Count} errors ===\n");
                var shown = Math.Max(0, Math.Min(budget, errors.Count));
                foreach (var entry in errors.Take(shown))
                    entry.AppendTo(text);

                if (shown < errors.Count)
                    text.AppendLine().Append($"(+{errors.Count - shown} more; raise maxDiagnostics to see them)");

                budget -= shown;
                AppendPartial(text);

                return text.ToString().TrimEnd();
            }

            private List<DiagnosticEntry> Ordered() =>
                _entries
                    .OrderBy(x => x.Severity == "error" ? 0 : 1)
                    .ThenBy(x => x.Line)
                    .ThenBy(x => x.Column)
                    .ToList();

            private void AppendPartial(StringBuilder text)
            {
                if (_inspectionFailures.Count == 0)
                    return;

                var count = _inspectionFailures.Values.Sum();
                var types = string.Join(", ", _inspectionFailures.Keys);
                text.AppendLine().AppendLine()
                    .Append($"ReSharper could not inspect {count} nodes ({types}); results for this file are partial");
            }

            private static DiagnosticEntry Entry(ITreeNode node, string severity, string message)
            {
                var range = TreeNodeExtensions.GetDocumentRange(node);
                if (!range.IsValid())
                    return null;

                var (line, column) = PsiHelpers.GetLineColumn(range.StartOffset);

                return new DiagnosticEntry
                {
                    Severity = severity,
                    Message = message,
                    Line = line,
                    Column = column,
                    Text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
                };
            }
        }

        private class DiagnosticEntry
        {
            public string Severity;
            public string Message;
            public int Line;
            public int Column;
            public string Text;

            public void AppendTo(StringBuilder text)
            {
                text.AppendLine();
                text.Append(Severity).Append(" :").Append(Line).Append(':').Append(Column);
                text.Append(" — ").AppendLine(Message);
                if (Text != null)
                    text.Append("  ").AppendLine(Text);
            }
        }
    }
}
