using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Application.Progress;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class ReplaceSymbolTool : IMcpWriteTool
    {
        private static readonly Regex IdentifierRegex = new Regex(@"^[_\p{L}][\p{L}\p{Nd}_]*$", RegexOptions.Compiled);

        private readonly ISolution _solution;

        public ReplaceSymbolTool(ISolution solution) => _solution = solution;

        public string Name => "replace_symbol";

        public string Description =>
            "Rename a source symbol and its resolvable references across the current solution. " +
            "Provide either a symbolName (e.g. 'MyClass' or 'Namespace.MyClass') or a file path with position, " +
            "plus the required newName. Uses PSI resolution; it does not perform plain text replacement. " +
            "Set dryRun=true to preview changes without modifying files. " +
            "Pass multiple symbols via the 'symbols' array to rename several symbols in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new
                {
                    type = "string",
                    description =
                        "Symbol name to rename (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column."
                },
                kind = new
                {
                    type = "string",
                    description =
                        "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name."
                },
                filePath = new
                {
                    type = "string", description = "Absolute path to the file containing the symbol or a usage of it"
                },
                line = new { type = "integer", description = "1-based line number of the symbol or usage" },
                column = new { type = "integer", description = "1-based column number of the symbol or usage" },
                newName = new { type = "string", description = "New identifier name for the symbol" },
                dryRun = new
                {
                    type = "boolean", description = "If true, preview changes without modifying files. Default: false."
                },
                maxResults = new { type = "integer", description = "Maximum changed locations to list. Default: 100." },
                symbols = new
                {
                    type = "array",
                    description =
                        "Array of symbols to rename in batch. Each item is an object with symbolName/kind or filePath/line/column and optional newName. Top-level newName is used for items that omit it.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Symbol name" },
                            kind = new { type = "string", description = "Symbol kind filter" },
                            filePath = new { type = "string", description = "File path" },
                            line = new { type = "integer", description = "1-based line" },
                            column = new { type = "integer", description = "1-based column" },
                            newName = new { type = "string", description = "New identifier name for this symbol" }
                        }
                    }
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var symbolsToken = arguments["symbols"] as JArray;
            if (symbolsToken != null && symbolsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < symbolsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = CloneObject(symbolsToken[i] as JObject);
                    CopyIfPresent(arguments, itemArgs, "newName");
                    CopyIfPresent(arguments, itemArgs, "dryRun");
                    CopyIfPresent(arguments, itemArgs, "maxResults");

                    var label = itemArgs["symbolName"]?.ToString() ??
                                $"{itemArgs["filePath"]}:{itemArgs["line"]}:{itemArgs["column"]}";
                    sb.Append("=== [").Append(i + 1).Append('/').Append(symbolsToken.Count)
                        .Append("] ").Append(label).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }

                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
        {
            var newName = arguments["newName"]?.ToString();
            if (string.IsNullOrEmpty(newName))
                return new { error = "newName is required" };
            if (!IdentifierRegex.IsMatch(newName))
                return new { error = $"Invalid identifier newName: {newName}" };

            var resolvedSymbol = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (resolvedSymbol.error != null) return resolvedSymbol.error;
            var declaredElement = resolvedSymbol.element;

            var oldName = declaredElement.ShortName;
            if (newName == oldName)
                return $"{PsiHelpers.GetQualifiedName(declaredElement)} already has name '{newName}'";

            var dryRun = arguments["dryRun"]?.Value<bool>() ?? false;
            var maxResults = arguments["maxResults"]?.Value<int>() ?? 100;
            if (maxResults <= 0) maxResults = 100;

            var replacements = new List<Replacement>();
            var skipped = new List<SkippedReference>();

            AddDeclarationReplacement(declaredElement, oldName, replacements, skipped);
            AddReferenceReplacements(declaredElement, oldName, replacements, skipped);

            replacements = replacements
                .GroupBy(r => r.File + ":" + r.StartOffset)
                .Select(g => g.First())
                .OrderBy(r => r.File)
                .ThenBy(r => r.StartOffset)
                .ToList();

            if (replacements.Count == 0)
                return new
                {
                    error = $"No safe source locations found for {PsiHelpers.GetQualifiedName(declaredElement)}"
                };

            if (!dryRun)
                ApplyReplacements(replacements, newName);

            return FormatResult(declaredElement, oldName, newName, dryRun, replacements, skipped, maxResults);
        }

        private void AddDeclarationReplacement(IDeclaredElement declaredElement, string oldName,
            List<Replacement> replacements, List<SkippedReference> skipped)
        {
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count == 0)
            {
                skipped.Add(new SkippedReference { Reason = "symbol has no source declarations" });
                return;
            }

            foreach (var declaration in declarations)
            {
                var nameNode = FindFirstLeafText(declaration, oldName);
                if (nameNode == null)
                {
                    skipped.Add(SkippedReference.FromNode(declaration, "could not locate declaration identifier"));
                    continue;
                }

                AddNodeReplacement(nameNode, oldName, "declaration", replacements, skipped);
            }
        }

        private void AddReferenceReplacements(IDeclaredElement declaredElement, string oldName,
            List<Replacement> replacements, List<SkippedReference> skipped)
        {
            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            psiServices.Finder.FindReferences(
                declaredElement,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (findResult is FindResultReference referenceResult)
                    {
                        var reference = referenceResult.Reference;
                        if (reference.GetName() != oldName)
                            return FindExecution.Continue;

                        if (reference.Resolve().DeclaredElement != declaredElement)
                            return FindExecution.Continue;

                        var node = reference.GetTreeNode();
                        AddNodeReplacement(node, oldName, "reference", replacements, skipped);
                    }

                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());
        }

        private static void AddNodeReplacement(ITreeNode node, string oldName, string kind,
            List<Replacement> replacements, List<SkippedReference> skipped)
        {
            var range = TreeNodeExtensions.GetDocumentRange(node);
            if (!range.IsValid())
            {
                skipped.Add(SkippedReference.FromNode(node, "invalid document range"));
                return;
            }

            var text = node.GetText();
            if (text != oldName)
            {
                skipped.Add(SkippedReference.FromNode(node,
                    $"unsafe node text '{PsiHelpers.TruncateSnippet(text, 80)}'"));
                return;
            }

            var sourceFile = node.GetSourceFile();
            var file = sourceFile?.GetLocation().FullPath;
            if (string.IsNullOrEmpty(file))
            {
                skipped.Add(SkippedReference.FromNode(node, "source file unavailable"));
                return;
            }

            var location = PsiHelpers.GetLineColumn(range.StartOffset);
            replacements.Add(new Replacement
            {
                Document = range.Document,
                File = file,
                Line = location.line,
                Column = location.column,
                StartOffset = range.StartOffset.Offset,
                EndOffset = range.EndOffset.Offset,
                Kind = kind
            });
        }

        private static ITreeNode FindFirstLeafText(ITreeNode node, string text)
        {
            if (node.FirstChild == null)
                return node.GetText() == text ? node : null;

            for (var child = node.FirstChild; child != null; child = child.NextSibling)
            {
                var found = FindFirstLeafText(child, text);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void ApplyReplacements(IEnumerable<Replacement> replacements, string newName)
        {
            foreach (var documentGroup in replacements.GroupBy(r => r.Document))
            {
                foreach (var replacement in documentGroup.OrderByDescending(r => r.StartOffset))
                    replacement.Document.ReplaceText(new TextRange(replacement.StartOffset, replacement.EndOffset),
                        newName);
            }
        }

        private static string FormatResult(IDeclaredElement declaredElement, string oldName, string newName,
            bool dryRun,
            List<Replacement> replacements, List<SkippedReference> skipped, int maxResults)
        {
            var sb = new StringBuilder();
            var filesTouched = replacements.Select(r => r.File).Distinct().Count();
            sb.Append(dryRun ? "dry run: " : "renamed: ");
            sb.Append(PsiHelpers.GetQualifiedName(declaredElement));
            sb.Append(" '").Append(oldName).Append("' -> '").Append(newName).Append("'").AppendLine();
            sb.Append(replacements.Count).Append(" locations in ").Append(filesTouched).AppendLine(" files");

            foreach (var replacement in replacements.Take(maxResults))
            {
                sb.Append("  ").Append(replacement.Kind).Append(": ")
                    .Append(replacement.File).Append(':').Append(replacement.Line).Append(':')
                    .AppendLine(replacement.Column.ToString());
            }

            if (replacements.Count > maxResults)
                sb.Append("  ... ").Append(replacements.Count - maxResults).AppendLine(" more locations omitted");

            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.Append("skipped ").Append(skipped.Count).AppendLine(" unsafe/non-source locations:");
                foreach (var item in skipped.Take(maxResults))
                {
                    sb.Append("  ").Append(item.Reason);
                    if (!string.IsNullOrEmpty(item.File))
                        sb.Append(" — ").Append(item.File).Append(':').Append(item.Line).Append(':')
                            .Append(item.Column);
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static JObject CloneObject(JObject source)
        {
            if (source == null) return new JObject();
            return (JObject)source.DeepClone();
        }

        private static void CopyIfPresent(JObject source, JObject target, string key)
        {
            var token = source[key];
            if (token != null && target[key] == null) target[key] = token;
        }

        private static string ResultToString(object result)
        {
            if (result is string s) return s;
            var jo = JObject.FromObject(result);
            return "error: " + (jo["error"]?.ToString() ?? result.ToString());
        }

        private class Replacement
        {
            public IDocument Document { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
            public int Column { get; set; }
            public int StartOffset { get; set; }
            public int EndOffset { get; set; }
            public string Kind { get; set; }
        }

        private class SkippedReference
        {
            public string File { get; set; }
            public int Line { get; set; }
            public int Column { get; set; }
            public string Reason { get; set; }

            public static SkippedReference FromNode(ITreeNode node, string reason)
            {
                var range = TreeNodeExtensions.GetDocumentRange(node);
                var skipped = new SkippedReference { Reason = reason };
                var sourceFile = node.GetSourceFile();
                skipped.File = sourceFile?.GetLocation().FullPath;
                if (range.IsValid())
                {
                    var location = PsiHelpers.GetLineColumn(range.StartOffset);
                    skipped.Line = location.line;
                    skipped.Column = location.column;
                }

                return skipped;
            }
        }
    }
}