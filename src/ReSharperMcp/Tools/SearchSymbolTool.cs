using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application;
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
            "Set 'includeLibraries' to also search types compiled into referenced DLLs (shown as [assembly Name.dll]). " +
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
                includeLibraries = new
                {
                    type = "boolean",
                    description = "Also search referenced assemblies (NuGet packages, DLLs): types by substring, and members of library types for dot-qualified queries. Default: false."
                },
                maxResults = new
                {
                    type = "integer",
                    description = "Maximum number of results to return per query. Default: 50"
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments) =>
            ToolBatch.Of(arguments, "queries", item: "query")?.Run(ExecuteSingle) ?? ExecuteSingle(arguments);

        private object ExecuteSingle(JObject arguments)
        {
            var query = arguments["query"]?.ToString();
            var kindsFilter = arguments["kinds"]?.ToString();
            var includeNamespaces = arguments["includeNamespaces"]?.Value<bool>() ?? false;
            var includeLibraries = arguments["includeLibraries"]?.Value<bool>() ?? false;
            var maxResults = arguments["maxResults"]?.Value<int>() ?? 50;

            if (string.IsNullOrEmpty(query))
                return new { error = "query is required" };

            if (maxResults <= 0) maxResults = 50;
            if (maxResults > 200) maxResults = 200;

            var search = new Search(ParseKinds(kindsFilter), includeNamespaces, maxResults);
            var symbols = _solution.GetPsiServices().Symbols;

            search.Run(symbols.GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true), query, scanMembers: true);

            // A member scan over every library type would take seconds; libraries answer type and Type.Member queries.
            if (includeLibraries)
                search.Run(symbols.GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true), query, scanMembers: false);

            // Format compact output
            var sb = new StringBuilder();
            sb.Append("query: ").Append(query).Append(" — ").Append(search.Results.Count).AppendLine(" results");

            foreach (var r in search.Results)
            {
                sb.AppendLine();
                sb.Append(r.Kind).Append(' ');
                if (r.ContainingType != null)
                    sb.Append(r.ContainingType).Append('.');
                sb.Append(r.Name);
                sb.Append(" — ").Append(r.Location);
            }

            if (search.Skipped.Line != null)
                sb.AppendLine().AppendLine().Append(search.Skipped.Line);

            return sb.ToString().TrimEnd();
        }

        private class SymbolResult
        {
            public string Name;
            public string Kind;
            public string ContainingType;
            public string Location;
        }

        /// <summary>
        /// One query's walk over one or more symbol scopes, sharing results, the result cap and skipped elements.
        /// Each type element is read under its own guard: one type ReSharper cannot read must not end the search.
        /// </summary>
        private sealed class Search
        {
            private readonly HashSet<string> _kinds;
            private readonly bool _includeNamespaces;
            private readonly int _max;
            private readonly HashSet<string> _seen = new HashSet<string>();

            public Search(HashSet<string> kinds, bool includeNamespaces, int max)
            {
                _kinds = kinds;
                _includeNamespaces = includeNamespaces;
                _max = max;
            }

            public List<SymbolResult> Results { get; } = new List<SymbolResult>();

            public SkippedElements Skipped { get; } = new SkippedElements();

            private bool IsFull => Results.Count >= _max;

            public void Run(ISymbolScope scope, string query, bool scanMembers)
            {
                // Support dot-qualified queries like "IProfile.Fake" → containingType="IProfile", memberName="Fake"
                var dotIndex = query.LastIndexOf('.');
                if (dotIndex > 0 && dotIndex < query.Length - 1)
                {
                    var containingType = query.Substring(0, dotIndex).ToLowerInvariant();
                    var memberName = query.Substring(dotIndex + 1).ToLowerInvariant();
                    SearchMembers(scope, typeName => typeName.Contains(containingType), memberName);
                    return;
                }

                var queryLower = query.ToLowerInvariant();
                var wantTypes = _kinds == null || _kinds.Contains("type") || _kinds.Contains("namespace");
                var wantMembers = _kinds == null || _kinds.Contains("method") || _kinds.Contains("property")
                                  || _kinds.Contains("field") || _kinds.Contains("event");

                if (wantTypes)
                    SearchTypes(scope, queryLower);

                if (wantMembers && scanMembers)
                    SearchMembers(scope, _ => true, queryLower);
            }

            private void SearchTypes(ISymbolScope scope, string queryLower)
            {
                foreach (var shortName in scope.GetAllShortNames())
                {
                    Interruption.Current.CheckAndThrow();
                    if (IsFull) break;
                    if (!shortName.ToLowerInvariant().Contains(queryLower)) continue;

                    Skipped.Guard(shortName, () =>
                    {
                        foreach (var element in scope.GetElementsByShortName(shortName))
                        {
                            if (IsFull) break;

                            if (element is INamespace && !_includeNamespaces &&
                                (_kinds == null || !_kinds.Contains("namespace")))
                                continue;

                            if (_kinds != null && !MatchesKindFilter(element, _kinds))
                                continue;

                            Skipped.Guard(element, () => Add(element));
                        }
                    });
                }
            }

            private void SearchMembers(ISymbolScope scope, System.Func<string, bool> typeNameMatches, string memberLower)
            {
                foreach (var shortName in scope.GetAllShortNames())
                {
                    Interruption.Current.CheckAndThrow();
                    if (IsFull) break;
                    if (!typeNameMatches(shortName.ToLowerInvariant())) continue;

                    Skipped.Guard(shortName, () =>
                    {
                        foreach (var element in scope.GetElementsByShortName(shortName))
                        {
                            if (IsFull) break;
                            if (element is ITypeElement typeElement)
                                Skipped.Guard(typeElement, () => AddMembers(typeElement, memberLower));
                        }
                    });
                }
            }

            private void AddMembers(ITypeElement typeElement, string memberLower)
            {
                foreach (var member in typeElement.GetMembers())
                {
                    if (IsFull) break;

                    var memberName = member.ShortName;
                    if (memberName == null) continue;
                    if (!memberName.ToLowerInvariant().Contains(memberLower)) continue;
                    if (_kinds != null && !MatchesKindFilter(member, _kinds)) continue;

                    Add(member);
                }
            }

            private void Add(IDeclaredElement element)
            {
                var (file, line, column) = FirstDeclaration(element);

                // Source-generated declarations have no file path; compiled ones have an assembly instead.
                var library = file == null ? PsiHelpers.AssemblyLabel(element) : null;
                var key = library != null ? $"{library}:{PsiHelpers.GetQualifiedName(element)}" : $"{file ?? "[generated]"}:{line}:{column}";
                if (!_seen.Add(key)) return;

                string containingType = null;
                if (element is IClrDeclaredElement clr)
                    containingType = clr.GetContainingType()?.ShortName;

                Results.Add(new SymbolResult
                {
                    Name = element.ShortName,
                    Kind = element.GetElementType().PresentableName,
                    ContainingType = containingType,
                    Location = library ?? $"{file ?? "[generated]"}:{line}"
                });
            }

            private static (string file, int line, int column) FirstDeclaration(IDeclaredElement element)
            {
                foreach (var d in element.GetDeclarations())
                {
                    var path = d.GetSourceFile()?.GetLocation().FullPath;
                    if (string.IsNullOrEmpty(path)) continue;

                    var r = TreeNodeExtensions.GetDocumentRange(d);
                    if (!r.IsValid()) continue;

                    var (line, column) = PsiHelpers.GetLineColumn(r.StartOffset);

                    return (path, line, column);
                }

                return (null, 0, 0);
            }
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
