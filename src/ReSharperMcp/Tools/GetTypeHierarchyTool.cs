using System.Collections.Generic;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetTypeHierarchyTool : IMcpTool
    {
        private readonly ISolution _solution;

        // Upper bound on the total number of type nodes emitted across the whole tree.
        // Protects against pathologically wide bases (e.g. object's subtypes).
        private const int NodeBudget = 500;

        public GetTypeHierarchyTool(ISolution solution) => _solution = solution;

        public string Name => "get_type_hierarchy";

        public string Description =>
            "Get the inheritance hierarchy of a type as a tree. " +
            "Direction 'supertypes' walks up to base classes and implemented interfaces; " +
            "direction 'subtypes' walks down to derived classes and implementors. " +
            "Provide either a symbolName (e.g. 'MyClass', 'Namespace.IMyInterface') or a file path with position. " +
            "Use maxDepth to bound recursion (default 3).";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Type name (e.g. 'MyClass', 'Namespace.IMyInterface'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the type" },
                line = new { type = "integer", description = "1-based line number of the type" },
                column = new { type = "integer", description = "1-based column number of the type" },
                direction = new { type = "string", description = "Required. 'supertypes' (walk up to base types) or 'subtypes' (walk down to derived types)." },
                maxDepth = new { type = "integer", description = "Maximum recursion depth (default 3)." }
            },
            required = new[] { "direction" }
        };

        public object Execute(JObject arguments)
        {
            var direction = arguments["direction"]?.ToString();
            if (string.IsNullOrEmpty(direction))
                return new { error = "Provide 'direction': either 'supertypes' or 'subtypes'." };

            direction = direction.ToLowerInvariant();
            if (direction != "supertypes" && direction != "subtypes")
                return new { error = $"Invalid direction '{direction}'. Use 'supertypes' or 'subtypes'." };

            var maxDepth = arguments["maxDepth"]?.Value<int>() ?? 3;
            if (maxDepth < 1) maxDepth = 1;

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            if (!(declaredElement is ITypeElement typeElement))
                return new { error = $"Symbol '{declaredElement.ShortName}' is not a type. Type hierarchy requires a class, interface, struct, or other type." };

            var budget = new NodeCounter(NodeBudget);
            // Track FQNs already expanded to avoid infinite loops on cyclic/diamond hierarchies.
            var visited = new HashSet<string>();
            var rootFqn = SafeFullName(typeElement);
            if (rootFqn != null) visited.Add(rootFqn);

            List<object> children = direction == "subtypes"
                ? BuildSubtypes(typeElement, 1, maxDepth, visited, budget)
                : BuildSupertypes(typeElement, 1, maxDepth, visited, budget);

            return new
            {
                type = typeElement.ShortName,
                qualifiedName = PsiHelpers.GetQualifiedName(typeElement),
                direction,
                maxDepth,
                truncated = budget.Exhausted,
                types = children
            };
        }

        // ---- Subtypes (walk down) ----

        private List<object> BuildSubtypes(
            ITypeElement typeElement, int depth, int maxDepth,
            HashSet<string> visited, NodeCounter budget)
        {
            var result = new List<object>();
            if (depth > maxDepth || budget.Exhausted) return result;

            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            var inheritors = new List<ITypeElement>();
            psiServices.Finder.FindInheritors(
                typeElement,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (findResult is FindResultInheritedElement inherited &&
                        inherited.DeclaredElement is ITypeElement inheritedType)
                        inheritors.Add(inheritedType);
                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());

            foreach (var inheritor in inheritors)
            {
                if (budget.Exhausted) break;

                // FindInheritors returns the full transitive closure (direct + indirect).
                // Only place an inheritor at THIS level if it directly inherits/implements
                // typeElement; indirect inheritors are nested under their direct base when
                // we recurse into that base. This prevents a transitive subtype from appearing
                // both nested under its intermediate base and again at the top level.
                if (!IsDirectInheritor(inheritor, typeElement)) continue;

                var fqn = SafeFullName(inheritor);
                // Skip if already expanded elsewhere in the tree (diamond inheritance, cycles).
                var alreadyVisited = fqn != null && !visited.Add(fqn);

                if (!budget.TryConsume()) break;

                var node = BuildNode(inheritor, RelationTo(inheritor, typeElement));

                if (!alreadyVisited)
                    node["children"] = BuildSubtypes(inheritor, depth + 1, maxDepth, visited, budget);
                else
                    node["children"] = new List<object>();

                result.Add(node);
            }

            return result;
        }

        // ---- Supertypes (walk up) ----

        private List<object> BuildSupertypes(
            ITypeElement typeElement, int depth, int maxDepth,
            HashSet<string> visited, NodeCounter budget)
        {
            var result = new List<object>();
            if (depth > maxDepth || budget.Exhausted) return result;

            foreach (var superType in typeElement.GetSuperTypes())
            {
                if (budget.Exhausted) break;

                var resolved = superType.GetTypeElement();
                if (resolved == null) continue;

                // Drop System.Object: every class extends it, so it's pure noise.
                // Real base classes and interfaces are kept.
                if (IsSystemObject(resolved)) continue;

                var fqn = SafeFullName(resolved);
                var alreadyVisited = fqn != null && !visited.Add(fqn);

                if (!budget.TryConsume()) break;

                // For supertypes, relation is determined by the supertype's own kind:
                // interfaces are "implements", everything else (classes) is "extends".
                var node = BuildNode(resolved, resolved is IInterface ? "implements" : "extends");

                if (!alreadyVisited)
                    node["children"] = BuildSupertypes(resolved, depth + 1, maxDepth, visited, budget);
                else
                    node["children"] = new List<object>();

                result.Add(node);
            }

            return result;
        }

        // ---- Helpers ----

        /// <summary>
        /// Determines the relation of a subtype to the (super) type it derives from:
        /// "implements" when the super type is an interface, otherwise "extends".
        /// </summary>
        private static string RelationTo(ITypeElement subtype, ITypeElement supertype)
        {
            return supertype is IInterface ? "implements" : "extends";
        }

        /// <summary>
        /// Checks if <paramref name="inheritor"/> directly lists <paramref name="baseType"/>
        /// among its declared super types (not reached via an intermediate type). Mirrors
        /// FindImplementationsTool.IsDirectImplementor so the hierarchy nests indirect
        /// inheritors under their direct base instead of duplicating them at the top level.
        /// </summary>
        private static bool IsDirectInheritor(ITypeElement inheritor, ITypeElement baseType)
        {
            var baseFqn = SafeFullName(baseType);
            if (baseFqn == null) return false;

            foreach (var superType in inheritor.GetSuperTypes())
            {
                var resolved = superType.GetTypeElement();
                if (resolved != null && SafeFullName(resolved) == baseFqn)
                    return true;
            }
            return false;
        }

        /// <summary>Returns true when the type is <c>System.Object</c>.</summary>
        private static bool IsSystemObject(ITypeElement element)
        {
            return SafeFullName(element) == "System.Object";
        }

        private static Dictionary<string, object> BuildNode(ITypeElement element, string relation)
        {
            var node = new Dictionary<string, object>
            {
                ["type"] = element.ShortName,
                ["qualifiedName"] = PsiHelpers.GetQualifiedName(element),
                ["relation"] = relation
            };

            var declarations = element.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var range = TreeNodeExtensions.GetDocumentRange(decl);
                var sourceFile = decl.GetSourceFile();
                if (range.IsValid() && sourceFile != null)
                {
                    var (line, column) = PsiHelpers.GetLineColumn(range.StartOffset);
                    node["file"] = sourceFile.GetLocation().FullPath;
                    node["line"] = line;
                    node["column"] = column;
                }
            }

            return node;
        }

        private static string SafeFullName(ITypeElement element)
        {
            var clrName = element.GetClrName();
            return clrName?.FullName;
        }

        /// <summary>
        /// Tracks how many type nodes have been emitted and whether the budget is exhausted.
        /// </summary>
        private sealed class NodeCounter
        {
            private int _remaining;

            public NodeCounter(int budget) => _remaining = budget;

            public bool Exhausted { get; private set; }

            /// <summary>Consumes one node from the budget. Returns false (and flags Exhausted) if none left.</summary>
            public bool TryConsume()
            {
                if (_remaining <= 0)
                {
                    Exhausted = true;
                    return false;
                }
                _remaining--;
                return true;
            }
        }
    }
}
