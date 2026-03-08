using System.Collections.Generic;
using System.Linq;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class SearchSymbolTool : IMcpTool
    {
        private readonly ISolution _solution;

        public SearchSymbolTool(ISolution solution) => _solution = solution;

        public string Name => "search_symbol";

        public string Description =>
            "Search for symbols (types, methods, properties, etc.) by name across the entire solution. " +
            "Supports partial/substring matching. Returns symbol locations that can be used with other tools.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "Symbol name to search for (supports partial matching)"
                },
                kinds = new
                {
                    type = "string",
                    description = "Comma-separated filter for symbol kinds: 'type', 'method', 'property', 'field', 'event', 'namespace'. Default: all kinds except namespaces."
                },
                includeNamespaces = new
                {
                    type = "boolean",
                    description = "Include namespace declarations in results. Default: false (namespaces are excluded to reduce noise)."
                },
                maxResults = new
                {
                    type = "integer",
                    description = "Maximum number of results to return. Default: 50"
                }
            },
            required = new[] { "query" }
        };

        public object Execute(JObject arguments)
        {
            var query = arguments["query"]?.ToString();
            var kindsFilter = arguments["kinds"]?.ToString();
            var includeNamespaces = arguments["includeNamespaces"]?.Value<bool>() ?? false;
            var maxResults = arguments["maxResults"]?.Value<int>() ?? 50;

            if (string.IsNullOrEmpty(query))
                return new { error = "query is required" };

            if (maxResults <= 0) maxResults = 50;
            if (maxResults > 200) maxResults = 200;

            var kindSet = ParseKinds(kindsFilter);
            var results = new List<object>();
            var queryLower = query.ToLowerInvariant();
            var seen = new HashSet<string>();

            foreach (var project in _solution.GetAllProjects())
            {
                if (results.Count >= maxResults) break;

                foreach (var projectFile in project.GetAllProjectFiles())
                {
                    if (results.Count >= maxResults) break;

                    foreach (var sourceFile in projectFile.ToSourceFiles())
                    {
                        if (results.Count >= maxResults) break;

                        var psiFile = PsiHelpers.GetPsiFile(sourceFile);
                        if (psiFile == null) continue;

                        foreach (var node in psiFile.Descendants().OfType<IDeclaration>())
                        {
                            if (results.Count >= maxResults) break;

                            var element = node.DeclaredElement;
                            if (element == null) continue;

                            // Exclude namespaces by default (they create noise)
                            if (element is INamespace && !includeNamespaces &&
                                (kindSet == null || !kindSet.Contains("namespace")))
                                continue;

                            var name = element.ShortName;
                            if (name == null) continue;

                            if (!name.ToLowerInvariant().Contains(queryLower))
                                continue;

                            if (kindSet != null && !MatchesKindFilter(element, kindSet))
                                continue;

                            var range = TreeNodeExtensions.GetDocumentRange(node);
                            if (!range.IsValid()) continue;

                            var declSourceFile = node.GetSourceFile();
                            if (declSourceFile == null) continue;

                            var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);

                            // Deduplicate by location
                            var key = $"{declSourceFile.GetLocation().FullPath}:{declLine}:{declCol}";
                            if (!seen.Add(key)) continue;

                            results.Add(new
                            {
                                name,
                                kind = element.GetElementType().PresentableName,
                                file = declSourceFile.GetLocation().FullPath,
                                line = declLine,
                                column = declCol,
                                text = PsiHelpers.TruncateSnippet(node.GetText())
                            });
                        }
                    }
                }
            }

            return new
            {
                query,
                resultsCount = results.Count,
                results
            };
        }

        private static HashSet<string> ParseKinds(string kindsFilter)
        {
            if (string.IsNullOrEmpty(kindsFilter)) return null;
            return new HashSet<string>(
                kindsFilter.Split(',').Select(k => k.Trim().ToLowerInvariant()));
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
