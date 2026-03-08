using System.Collections.Generic;
using System.Linq;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
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
            "Supports partial/substring matching. Dot-qualified queries like 'IProfile.Fake' match " +
            "members by ContainingType.MemberName. Returns symbol locations that can be used with other tools.";

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
            var seen = new HashSet<string>();

            var psiServices = _solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);

            // Support dot-qualified queries like "IProfile.Fake" → containingType="IProfile", memberName="Fake"
            string qualifiedContainingType = null;
            string qualifiedMemberName = null;
            string queryLower;

            var dotIndex = query.LastIndexOf('.');
            if (dotIndex > 0 && dotIndex < query.Length - 1)
            {
                qualifiedContainingType = query.Substring(0, dotIndex).ToLowerInvariant();
                qualifiedMemberName = query.Substring(dotIndex + 1).ToLowerInvariant();
                queryLower = qualifiedMemberName;
            }
            else
            {
                queryLower = query.ToLowerInvariant();
            }

            var wantTypes = kindSet == null || kindSet.Contains("type") || kindSet.Contains("namespace");
            var wantMembers = kindSet == null || kindSet.Contains("method") || kindSet.Contains("property")
                              || kindSet.Contains("field") || kindSet.Contains("event");

            if (qualifiedContainingType != null)
            {
                // Dot-qualified: find containing types, then search their members
                SearchDotQualified(symbolScope, qualifiedContainingType, qualifiedMemberName,
                    kindSet, includeNamespaces, maxResults, results, seen);
            }
            else
            {
                // Search types/namespaces from cache
                if (wantTypes)
                {
                    SearchTypes(symbolScope, queryLower, kindSet, includeNamespaces, maxResults, results, seen);
                }

                // Search members (methods, properties, etc.) via type cache
                if (wantMembers && results.Count < maxResults)
                {
                    SearchMembers(symbolScope, queryLower, kindSet, maxResults, results, seen);
                }
            }

            return new
            {
                query,
                resultsCount = results.Count,
                results
            };
        }

        private void SearchTypes(ISymbolScope symbolScope, string queryLower,
            HashSet<string> kindSet, bool includeNamespaces, int maxResults,
            List<object> results, HashSet<string> seen)
        {
            foreach (var shortName in symbolScope.GetAllShortNames())
            {
                if (results.Count >= maxResults) break;
                if (!shortName.ToLowerInvariant().Contains(queryLower)) continue;

                foreach (var element in symbolScope.GetElementsByShortName(shortName))
                {
                    if (results.Count >= maxResults) break;

                    if (element is INamespace && !includeNamespaces &&
                        (kindSet == null || !kindSet.Contains("namespace")))
                        continue;

                    if (kindSet != null && !MatchesKindFilter(element, kindSet))
                        continue;

                    AddElementResult(element, results, seen);
                }
            }
        }

        private void SearchMembers(ISymbolScope symbolScope, string queryLower,
            HashSet<string> kindSet, int maxResults,
            List<object> results, HashSet<string> seen)
        {
            // Iterate all types from the cache and check their members
            foreach (var shortName in symbolScope.GetAllShortNames())
            {
                if (results.Count >= maxResults) break;

                foreach (var element in symbolScope.GetElementsByShortName(shortName))
                {
                    if (results.Count >= maxResults) break;
                    if (!(element is ITypeElement typeElement)) continue;

                    foreach (var member in typeElement.GetMembers())
                    {
                        if (results.Count >= maxResults) break;

                        var memberName = member.ShortName;
                        if (memberName == null) continue;
                        if (!memberName.ToLowerInvariant().Contains(queryLower)) continue;
                        if (kindSet != null && !MatchesKindFilter(member, kindSet)) continue;

                        AddElementResult(member, results, seen);
                    }
                }
            }
        }

        private void SearchDotQualified(ISymbolScope symbolScope,
            string containingTypeLower, string memberNameLower,
            HashSet<string> kindSet, bool includeNamespaces, int maxResults,
            List<object> results, HashSet<string> seen)
        {
            foreach (var shortName in symbolScope.GetAllShortNames())
            {
                if (results.Count >= maxResults) break;
                if (!shortName.ToLowerInvariant().Contains(containingTypeLower)) continue;

                foreach (var element in symbolScope.GetElementsByShortName(shortName))
                {
                    if (results.Count >= maxResults) break;
                    if (!(element is ITypeElement typeElement)) continue;

                    foreach (var member in typeElement.GetMembers())
                    {
                        if (results.Count >= maxResults) break;

                        var memberName = member.ShortName;
                        if (memberName == null) continue;
                        if (!memberName.ToLowerInvariant().Contains(memberNameLower)) continue;
                        if (kindSet != null && !MatchesKindFilter(member, kindSet)) continue;

                        AddElementResult(member, results, seen);
                    }
                }
            }
        }

        private static void AddElementResult(IDeclaredElement element,
            List<object> results, HashSet<string> seen)
        {
            var declarations = element.GetDeclarations();
            if (declarations.Count == 0) return;

            var decl = declarations[0];
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid()) return;

            var sourceFile = decl.GetSourceFile();
            if (sourceFile == null) return;

            var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
            var key = $"{sourceFile.GetLocation().FullPath}:{line}:{col}";
            if (!seen.Add(key)) return;

            var resultEntry = new Dictionary<string, object>
            {
                ["name"] = element.ShortName,
                ["kind"] = element.GetElementType().PresentableName,
                ["file"] = sourceFile.GetLocation().FullPath,
                ["line"] = line,
                ["column"] = col,
                ["text"] = PsiHelpers.TruncateSnippet(decl.GetText())
            };

            if (element is IClrDeclaredElement clr)
            {
                var ct = clr.GetContainingType();
                if (ct != null)
                    resultEntry["containingType"] = ct.ShortName;
            }

            results.Add(resultEntry);
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
            if (element is INamespace && kindSet.Contains("namespace")) return true;
            return false;
        }
    }
}
