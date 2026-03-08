using System.Collections.Generic;
using System.Linq;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class ListSymbolsInFileTool : IMcpTool
    {
        private readonly ISolution _solution;

        public ListSymbolsInFileTool(ISolution solution) => _solution = solution;

        public string Name => "list_symbols_in_file";

        public string Description =>
            "List all symbols declared in a file: types, methods, properties, fields, events. " +
            "Provides a structural overview of a file without reading the full source.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new { type = "string", description = "Absolute path to the file" },
                kinds = new { type = "string", description = "Comma-separated filter: 'type', 'method', 'property', 'field', 'event'. Default: all (excluding locals/parameters)." },
                includeLocals = new { type = "boolean", description = "Include local variables and parameters. Default: false." }
            },
            required = new[] { "filePath" }
        };

        public object Execute(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            var kindsFilter = arguments["kinds"]?.ToString();
            var includeLocals = arguments["includeLocals"]?.Value<bool>() ?? false;

            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return new { error = "Could not get PSI tree for file" };

            var kindSet = !string.IsNullOrEmpty(kindsFilter)
                ? new HashSet<string>(kindsFilter.Split(',').Select(k => k.Trim().ToLowerInvariant()))
                : null;

            var symbols = new List<object>();
            var seen = new HashSet<string>();

            foreach (var node in psiFile.Descendants().OfType<IDeclaration>())
            {
                var element = node.DeclaredElement;
                if (element == null) continue;
                if (element is INamespace) continue;

                // Exclude local variables and parameters by default
                if (!includeLocals && (element is ILocalVariable || element is IParameter))
                    continue;

                if (kindSet != null && !MatchesKindFilter(element, kindSet))
                    continue;

                var range = TreeNodeExtensions.GetDocumentRange(node);
                if (!range.IsValid()) continue;

                var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);

                // Deduplicate
                var key = $"{line}:{col}";
                if (!seen.Add(key)) continue;

                var symbolInfo = new Dictionary<string, object>
                {
                    ["name"] = element.ShortName,
                    ["kind"] = element.GetElementType().PresentableName,
                    ["line"] = line,
                    ["column"] = col
                };

                // Add containing type for members
                if (element is IClrDeclaredElement clr)
                {
                    var containingType = clr.GetContainingType();
                    if (containingType != null)
                        symbolInfo["containingType"] = containingType.ShortName;
                }

                symbols.Add(symbolInfo);
            }

            return new
            {
                file = filePath,
                symbolCount = symbols.Count,
                symbols
            };
        }

        private static bool MatchesKindFilter(IDeclaredElement element, HashSet<string> kindSet)
        {
            if (element is ITypeElement && kindSet.Contains("type")) return true;
            if (element is IMethod && kindSet.Contains("method")) return true;
            if (element is IProperty && kindSet.Contains("property")) return true;
            if (element is IField && kindSet.Contains("field")) return true;
            if (element is IEvent && kindSet.Contains("event")) return true;
            return false;
        }
    }
}
