using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.DocumentModel;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class FindUsagesTool : IMcpTool
    {
        private readonly ISolution _solution;

        public FindUsagesTool(ISolution solution) => _solution = solution;

        private const int DefaultMaxResults = 100;

        // Usages collected per symbol before counting stops; "N+" is reported beyond it.
        private const int SearchCap = 5000;

        private const int SummarizedFiles = 20;

        public string Name => "find_usages";

        public string Description =>
            "Find all usages/references of a code symbol (class, method, property, variable, etc.) " +
            "in the current solution. Provide either a symbolName (e.g. 'MyClass', 'MyClass.MyMethod') " +
            "or a file path with position (line/column). " +
            "Default output is rg-shaped: usages grouped by file (paths relative to the solution root), one " +
            "'line: code' row each. mode 'files' lists files with counts; mode 'grouped' groups by project. " +
            "Narrow with 'pathPrefix' and 'excludeTests'. " +
            "Pass multiple symbols via the 'symbols' array to search for several in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to find usages of (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                excludeDeclarationFile = new { type = "boolean", description = "Exclude usages from the file(s) where the symbol is declared. Useful for finding consumers rather than definition-site references. Default: false." },
                maxResults = new { type = "integer", description = $"Maximum usage lines to print (files in mode 'files'). The total is always counted; the rest is summarized as top files. Default: {DefaultMaxResults}." },
                mode = new { type = "string", description = "'lines' (default): rg-shaped 'path' then '  line: code' rows. 'files': file paths with usage counts. 'grouped': grouped by project, then file name." },
                pathPrefix = new { type = "string", description = "Only usages in files under this path, relative to the solution root (e.g. 'Shared/Battle') or absolute." },
                excludeTests = new { type = "boolean", description = "Skip usages in test code: projects or folders named Test/Tests/*.Tests. Default: false." },
                symbols = new
                {
                    type = "array",
                    description = "Array of symbols to find usages for in batch. Each item is an object with symbolName/kind or filePath/line/column. Results are concatenated with separators.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Symbol name" },
                            kind = new { type = "string", description = "Symbol kind filter" },
                            filePath = new { type = "string", description = "File path" },
                            line = new { type = "integer", description = "1-based line" },
                            column = new { type = "integer", description = "1-based column" }
                        }
                    }
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments) =>
            ToolBatch.Of(arguments, "symbols", item: "symbolName")?.Run(ExecuteSingle) ?? ExecuteSingle(arguments);

        private object ExecuteSingle(JObject arguments)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;
            using var context = CompilationContexts.For(declaredElement);

            var excludeDeclFile = arguments["excludeDeclarationFile"]?.Value<bool>() ?? false;
            var excludeTests = arguments["excludeTests"]?.Value<bool>() ?? false;
            var pathPrefix = PathPrefix(arguments["pathPrefix"]?.ToString());
            var mode = arguments["mode"]?.ToString() ?? "lines";
            var maxResults = arguments["maxResults"]?.Value<int>() ?? DefaultMaxResults;
            if (maxResults <= 0) maxResults = DefaultMaxResults;
            var hitLimit = false;

            // Collect declaration file paths for exclusion
            var declFilePaths = new HashSet<string>();
            if (excludeDeclFile)
            {
                foreach (var decl in declaredElement.GetDeclarations())
                {
                    var sf = decl.GetSourceFile();
                    if (sf != null)
                        declFilePaths.Add(sf.GetLocation().FullPath);
                }
            }

            // Collect raw usages
            var rawUsages = new List<RawUsage>();
            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            // Shared consumer logic for all FindReferences calls
            FindExecution HandleResult(FindResult findResult)
            {
                if (rawUsages.Count >= SearchCap)
                {
                    hitLimit = true;
                    return FindExecution.Stop;
                }
                if (findResult is FindResultReference reference)
                {
                    var refNode = reference.Reference.GetTreeNode();
                    var refSourceFile = refNode.GetSourceFile();
                    if (refSourceFile != null)
                    {
                        var filePath = refSourceFile.GetLocation().FullPath;
                        if (string.IsNullOrEmpty(filePath))
                            return FindExecution.Continue;

                        if (excludeDeclFile && declFilePaths.Contains(filePath))
                            return FindExecution.Continue;

                        if (pathPrefix != null && !filePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                            return FindExecution.Continue;

                        var projectName = refSourceFile.GetProject()?.Name ?? "(unknown)";
                        if (excludeTests && IsTest(projectName, Relative(filePath)))
                            return FindExecution.Continue;

                        var refRange = TreeNodeExtensions.GetDocumentRange(refNode);
                        if (refRange.IsValid())
                        {
                            var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                            rawUsages.Add(new RawUsage
                            {
                                Project = projectName,
                                File = filePath,
                                Line = refLine,
                                Column = refCol,
                                Code = LineText(refRange.Document, refLine),
                                Text = mode == "grouped"
                                    ? PsiHelpers.TruncateSnippet(refNode.Parent?.GetText() ?? refNode.GetText())
                                    : null
                            });
                        }
                    }
                }
                return FindExecution.Continue;
            }

            // Find direct usages
            psiServices.Finder.FindReferences(
                declaredElement,
                searchDomain,
                new FindResultConsumer(HandleResult),
                NullProgressIndicator.Create());

            // Also search for usages of interface/base methods this element implements
            var superMembers = FindInterfaceMembers(declaredElement);
            foreach (var superMember in superMembers)
            {
                if (hitLimit) break;
                psiServices.Finder.FindReferences(
                    superMember,
                    searchDomain,
                    new FindResultConsumer(HandleResult),
                    NullProgressIndicator.Create());
            }

            // Deduplicate: keep only one usage per line per file
            var deduped = rawUsages
                .GroupBy(u => $"{u.File}:{u.Line}")
                .Select(g => g.First())
                .ToList();

            var sb = new StringBuilder();
            var fileCount = deduped.Select(u => u.File).Distinct().Count();
            var projectCount = deduped.Select(u => u.Project).Distinct().Count();

            sb.Append(declaredElement.GetElementType().PresentableName).Append(' ');
            sb.Append(declaredElement.ShortName);
            sb.Append(" — ").Append(deduped.Count);
            if (hitLimit) sb.Append('+');
            sb.Append(" usages in ");
            sb.Append(fileCount).Append(" files, ");
            sb.Append(projectCount).AppendLine(" projects");
            if (hitLimit)
                sb.AppendLine($"(stopped counting at {SearchCap}; narrow with pathPrefix or excludeTests)");

            if (superMembers.Count > 0)
            {
                sb.Append("(includes usages via: ");
                sb.Append(string.Join(", ", superMembers.Select(m => PsiHelpers.GetQualifiedName(m))));
                sb.AppendLine(")");
            }

            // When the element is an implementation/override and we found few or no direct usages,
            // suggest searching the interface/base member directly for better results
            if (deduped.Count == 0 && superMembers.Count == 0)
            {
                var implNote = GetImplementationNote(declaredElement);
                if (implNote != null)
                    sb.AppendLine(implNote);
            }

            var (declPath, declLine) = PsiHelpers.Declaration(declaredElement);
            sb.Append("declared: ")
              .AppendLine(declPath != null ? $"{Relative(declPath)}:{declLine}" : PsiHelpers.Location(declaredElement));

            switch (mode)
            {
                case "files":
                    AppendFiles(sb, deduped, maxResults);
                    break;

                case "grouped":
                    AppendGrouped(sb, deduped.Take(maxResults).ToList());
                    AppendRest(sb, deduped.Skip(maxResults).ToList());
                    break;

                default:
                    AppendLines(sb, deduped, maxResults);
                    break;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// rg-shaped: a relative path, then "  line: code" per usage, in path and line order.
        /// </summary>
        private void AppendLines(StringBuilder sb, List<RawUsage> usages, int max)
        {
            var ordered = usages
                .OrderBy(u => Relative(u.File), StringComparer.Ordinal)
                .ThenBy(u => u.Line)
                .ToList();

            foreach (var file in ordered.Take(max).GroupBy(u => u.File))
            {
                sb.AppendLine().AppendLine(Relative(file.Key));
                foreach (var usage in file)
                    sb.Append("  ").Append(usage.Line).Append(": ").AppendLine(usage.Code);
            }

            AppendRest(sb, ordered.Skip(max).ToList());
        }

        private void AppendFiles(StringBuilder sb, List<RawUsage> usages, int max)
        {
            var files = usages
                .GroupBy(u => u.File)
                .OrderBy(g => Relative(g.Key), StringComparer.Ordinal)
                .ToList();

            sb.AppendLine();
            foreach (var file in files.Take(max))
                sb.Append(Relative(file.Key)).Append(" (").Append(file.Count()).AppendLine(")");

            if (files.Count > max)
                sb.AppendLine($"… {files.Count - max} more files (raise maxResults)");
        }

        private void AppendGrouped(StringBuilder sb, List<RawUsage> usages)
        {
            // Group by project → file
            var grouped = usages
                .GroupBy(u => u.Project)
                .OrderByDescending(g => g.Count());

            foreach (var projectGroup in grouped)
            {
                sb.AppendLine();
                sb.Append("--- ").Append(projectGroup.Key).Append(" (")
                  .Append(projectGroup.Count()).AppendLine(" usages) ---");

                foreach (var fileGroup in projectGroup.GroupBy(u => u.File).OrderByDescending(g => g.Count()))
                {
                    sb.AppendLine(Path.GetFileName(fileGroup.Key));
                    foreach (var u in fileGroup)
                    {
                        sb.Append("  :").Append(u.Line).Append(':').Append(u.Column)
                          .Append(" — ").AppendLine(u.Text);
                    }
                }
            }
        }

        /// <summary>
        /// Usages past the cap, as a count and the files holding most of them, instead of overflowing the caller.
        /// </summary>
        private void AppendRest(StringBuilder sb, List<RawUsage> rest)
        {
            if (rest.Count == 0)
                return;

            var files = rest.GroupBy(u => u.File).OrderByDescending(g => g.Count()).ToList();
            sb.AppendLine();
            sb.AppendLine($"… {rest.Count} more usages in {files.Count} files (raise maxResults, or narrow with pathPrefix / excludeTests):");
            foreach (var file in files.Take(SummarizedFiles))
                sb.Append("  ").Append(Relative(file.Key)).Append(" (").Append(file.Count()).AppendLine(")");

            if (files.Count > SummarizedFiles)
                sb.AppendLine($"  … {files.Count - SummarizedFiles} more files");
        }

        private string Relative(string path) =>
            SolutionRoot.Of(_solution).Relative(path);

        private string PathPrefix(string prefix) =>
            string.IsNullOrWhiteSpace(prefix) ? null : SolutionRoot.Of(_solution).Absolute(prefix);

        /// <remarks>
        /// Test code by convention: a project named *Test/*Tests, or a folder named Test/Tests/*.Tests on the path.
        /// </remarks>
        private static bool IsTest(string project, string relativePath)
        {
            if (project.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                || project.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
                return true;

            var folders = relativePath.Split('/', '\\');

            return folders.Take(folders.Length - 1).Any(x =>
                x.Equals("Test", StringComparison.OrdinalIgnoreCase)
                || x.Equals("Tests", StringComparison.OrdinalIgnoreCase)
                || x.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));
        }

        private static string LineText(IDocument document, int line)
        {
            var docLine = (Int32<DocLine>)(line - 1);
            var start = document.GetLineStartOffset(docLine);
            var end = document.GetLineEndOffsetNoLineBreak(docLine);

            return PsiHelpers.TruncateSnippet(document.GetText(new TextRange(start, end)), 300);
        }

        /// <summary>
        /// Finds interface methods that the given element implements.
        /// Walks the full type hierarchy to find all transitive interface members.
        /// </summary>
        private static List<IDeclaredElement> FindInterfaceMembers(IDeclaredElement element)
        {
            var result = new List<IDeclaredElement>();
            if (!(element is IClrDeclaredElement clrElem)) return result;

            var containingType = clrElem.GetContainingType();
            if (containingType == null) return result;

            var memberName = element.ShortName;
            var paramCount = (element as IParametersOwner)?.Parameters.Count ?? -1;
            var visited = new HashSet<string>();

            CollectInterfaceMembers(containingType, memberName, paramCount, element, result, visited);
            return result;
        }

        private static void CollectInterfaceMembers(ITypeElement type, string memberName, int paramCount,
            IDeclaredElement originalMember, List<IDeclaredElement> result, HashSet<string> visited)
        {
            foreach (var superType in type.GetSuperTypes())
            {
                var superElement = superType.GetTypeElement();
                if (superElement == null) continue;

                var fqn = superElement.GetClrName().FullName;
                if (!visited.Add(fqn)) continue;

                // Only collect members from interfaces
                if (superElement is IInterface)
                {
                    foreach (var m in superElement.GetMembers())
                    {
                        if (m.ShortName != memberName) continue;
                        if (m.Equals(originalMember)) continue;
                        if (paramCount >= 0 && m is IParametersOwner po && po.Parameters.Count != paramCount)
                            continue;
                        result.Add(m);
                    }
                }

                // Recurse into all super types to find transitive interfaces
                CollectInterfaceMembers(superElement, memberName, paramCount, originalMember, result, visited);
            }
        }

        /// <summary>
        /// If the element is an implementation/override of an interface or base class member,
        /// returns a note suggesting the user search the base member instead.
        /// </summary>
        private static string GetImplementationNote(IDeclaredElement element)
        {
            if (!(element is IOverridableMember overridable)) return null;

            var superMembers = overridable.GetImmediateSuperMembers();
            if (superMembers == null) return null;

            var notes = new List<string>();
            foreach (var superMemberInstance in superMembers)
            {
                var superMember = superMemberInstance.Member;
                if (superMember == null) continue;
                notes.Add(PsiHelpers.GetQualifiedName(superMember));
            }

            if (notes.Count == 0) return null;

            return "Note: This is an implementation/override of " +
                   string.Join(", ", notes) +
                   ". Use find_usages on the interface/base member to find call sites.";
        }

        private class RawUsage
        {
            public string Project;
            public string File;
            public int Line;
            public int Column;
            public string Code;
            public string Text;
        }
    }
}
