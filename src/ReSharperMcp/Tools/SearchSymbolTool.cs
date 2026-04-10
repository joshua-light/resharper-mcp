using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            "members by ContainingType.MemberName. Returns symbol locations that can be used with other tools. " +
            "Pass multiple queries via the 'queries' array to search for several symbols in one call.";

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
                queries = new
                {
                    type = "array",
                    description = "Array of symbol names to search for in batch. Results are concatenated with separators. Alternative to single 'query' parameter.",
                    items = new { type = "string" }
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
                    description = "Maximum number of results to return per query. Default: 50"
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var queriesToken = arguments["queries"] as JArray;
            if (queriesToken != null && queriesToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < queriesToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = new JObject();
                    itemArgs["query"] = queriesToken[i]?.ToString();
                    CopyIfPresent(arguments, itemArgs, "kinds");
                    CopyIfPresent(arguments, itemArgs, "includeNamespaces");
                    CopyIfPresent(arguments, itemArgs, "maxResults");

                    sb.Append("=== [").Append(i + 1).Append('/').Append(queriesToken.Count)
                      .Append("] ").Append(queriesToken[i]).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
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
            var results = new List<SymbolResult>();
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
                SearchDotQualified(symbolScope, qualifiedContainingType, qualifiedMemberName,
                    kindSet, includeNamespaces, maxResults, results, seen);
            }
            else
            {
                if (wantTypes)
                {
                    SearchTypes(symbolScope, queryLower, kindSet, includeNamespaces, maxResults, results, seen);
                }

                if (wantMembers && results.Count < maxResults)
                {
                    SearchMembers(symbolScope, queryLower, kindSet, maxResults, results, seen);
                }
            }

            // Format compact output
            var sb = new StringBuilder();
            sb.Append("query: ").Append(query).Append(" — ").Append(results.Count).AppendLine(" results");

            foreach (var r in results)
            {
                sb.AppendLine();
                sb.Append(r.Kind).Append(' ');
                if (r.ContainingType != null)
                    sb.Append(r.ContainingType).Append('.');
                sb.Append(r.Name);
                sb.Append(" — ").Append(r.File).Append(':').Append(r.Line);
            }

            return sb.ToString().TrimEnd();
        }

        private static void CopyIfPresent(JObject source, JObject target, string key)
        {
            var token = source[key];
            if (token != null) target[key] = token;
        }

        private static string ResultToString(object result)
        {
            if (result is string s) return s;
            var jo = JObject.FromObject(result);
            return "error: " + (jo["error"]?.ToString() ?? result.ToString());
        }

        private class SymbolResult
        {
            public string Name;
            public string Kind;
            public string ContainingType;
            public string File;
            public int Line;
        }

        private void SearchTypes(ISymbolScope symbolScope, string queryLower,
            HashSet<string> kindSet, bool includeNamespaces, int maxResults,
            List<SymbolResult> results, HashSet<string> seen)
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
            List<SymbolResult> results, HashSet<string> seen)
        {
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
            List<SymbolResult> results, HashSet<string> seen)
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
            List<SymbolResult> results, HashSet<string> seen)
        {
            // Find the first declaration with a valid file path
            string filePath = null;
            int line = 0, col = 0;
            foreach (var d in element.GetDeclarations())
            {
                var sf = d.GetSourceFile();
                var path = sf?.GetLocation().FullPath;
                if (string.IsNullOrEmpty(path)) continue;

                var r = TreeNodeExtensions.GetDocumentRange(d);
                if (!r.IsValid()) continue;

                filePath = path;
                var pos = PsiHelpers.GetLineColumn(r.StartOffset);
                line = pos.line;
                col = pos.column;
                break;
            }

            // Annotate source-generated types that have no navigable file path
            if (filePath == null)
            {
                filePath = "[generated]";
                line = 0;
                col = 0;
            }

            var key = $"{filePath}:{line}:{col}";
            if (!seen.Add(key)) return;

            string containingType = null;
            if (element is IClrDeclaredElement clr)
            {
                var ct = clr.GetContainingType();
                if (ct != null)
                    containingType = ct.ShortName;
            }

            results.Add(new SymbolResult
            {
                Name = element.ShortName,
                Kind = element.GetElementType().PresentableName,
                ContainingType = containingType,
                File = filePath,
                Line = line
            });
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
