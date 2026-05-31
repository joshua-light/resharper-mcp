using System.Collections.Generic;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetCallHierarchyTool : IMcpTool
    {
        // Cap on the total number of nodes we add to the tree, to keep the
        // operation bounded well inside the 30s R# read-lock budget.
        private const int NodeBudget = 300;

        // Hard cap on the requested depth.
        private const int MaxDepthCap = 4;

        private readonly ISolution _solution;

        public GetCallHierarchyTool(ISolution solution) => _solution = solution;

        public string Name => "get_call_hierarchy";

        public string Description =>
            "Build a call hierarchy tree for a method/function. " +
            "'incoming' finds callers (who calls this method, recursively); " +
            "'outgoing' finds callees (which methods this method calls, recursively). " +
            "Provide either a symbolName (e.g. 'MyClass.MyMethod') or a file path with position (line/column), " +
            "plus a 'direction'. Recursion is bounded by 'maxDepth' (default 2, capped at 4) and an internal node budget. " +
            "Outgoing resolution is C#-specific; non-C# roots degrade gracefully.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Method/function name to build the hierarchy from (e.g. 'MyClass.MyMethod', 'Namespace.MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the method" },
                line = new { type = "integer", description = "1-based line number of the method" },
                column = new { type = "integer", description = "1-based column number of the method" },
                direction = new { type = "string", description = "'incoming' (callers of the method) or 'outgoing' (methods called by the method). Required." },
                maxDepth = new { type = "integer", description = "How many levels deep to recurse (default 2, capped at 4)." }
            },
            required = new[] { "direction" }
        };

        public object Execute(JObject arguments)
        {
            var direction = arguments["direction"]?.ToString()?.Trim().ToLowerInvariant();
            if (direction != "incoming" && direction != "outgoing")
                return new { error = "Provide 'direction' as either 'incoming' or 'outgoing'." };

            var maxDepth = arguments["maxDepth"]?.Value<int>() ?? 2;
            if (maxDepth < 1) maxDepth = 1;
            if (maxDepth > MaxDepthCap) maxDepth = MaxDepthCap;

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            // Only method/function-like elements have a call hierarchy.
            if (!(declaredElement is IFunction function))
                return new
                {
                    error = $"Symbol '{declaredElement.ShortName}' is a " +
                            $"{declaredElement.GetElementType().PresentableName}, not a method/function. " +
                            "Call hierarchy is only available for methods and functions."
                };

            var budget = new NodeBudgetCounter();
            var visited = new HashSet<string>();
            object note = null;

            List<object> calls;
            if (direction == "incoming")
            {
                calls = BuildIncoming(function, 0, maxDepth, visited, budget);
            }
            else
            {
                var decls = function.GetDeclarations();
                ICSharpFunctionDeclaration csDecl = null;
                foreach (var decl in decls)
                {
                    if (decl is ICSharpFunctionDeclaration f)
                    {
                        csDecl = f;
                        break;
                    }
                }

                if (csDecl == null)
                {
                    // Non-C# root (or no source declaration available): outgoing analysis
                    // is C#-specific, so degrade gracefully with an empty list and a note.
                    calls = new List<object>();
                    note = "Outgoing call resolution is only supported for C# methods with an available source declaration.";
                }
                else
                {
                    calls = BuildOutgoing(csDecl, 0, maxDepth, visited, budget);
                }
            }

            var result = new Dictionary<string, object>
            {
                ["symbol"] = function.ShortName,
                ["qualifiedName"] = PsiHelpers.GetQualifiedName(function),
                ["direction"] = direction,
                ["maxDepth"] = maxDepth,
                ["truncated"] = budget.Truncated,
                ["calls"] = calls
            };

            if (note != null)
                result["note"] = note;

            return result;
        }

        // ---- Incoming (callers) -------------------------------------------------

        private List<object> BuildIncoming(
            IFunction function, int depth, int maxDepth, HashSet<string> visited, NodeBudgetCounter budget)
        {
            var children = new List<object>();
            if (depth >= maxDepth || budget.Exhausted)
                return children;

            // Cycle detection: do not expand a function we are already expanding
            // along the current path / have already expanded as a node.
            var fqn = PsiHelpers.GetQualifiedName(function);
            if (!visited.Add(fqn))
                return children;

            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            // Dedup callers by their qualified name so the same calling method only
            // appears once even if it calls the target multiple times.
            var seenCallers = new HashSet<string>();
            var callerEntries = new List<(IFunction callerFn, Dictionary<string, object> node)>();

            psiServices.Finder.FindReferences(
                function,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (budget.Exhausted)
                        return FindExecution.Stop;

                    if (!(findResult is FindResultReference reference))
                        return FindExecution.Continue;

                    var refNode = reference.Reference.GetTreeNode();
                    if (refNode == null)
                        return FindExecution.Continue;

                    // Treat the reference as a call only if it is the invoked expression
                    // of an invocation; otherwise it's a plain reference (e.g. method group).
                    var asExpr = refNode as ICSharpExpression;
                    var isCall = asExpr != null && InvocationExpressionNavigator.GetByInvokedExpression(asExpr) != null;

                    var callerFn = refNode.GetContainingNode<ICSharpFunctionDeclaration>()?.DeclaredElement;
                    if (callerFn == null)
                        return FindExecution.Continue;

                    var callerFqn = PsiHelpers.GetQualifiedName(callerFn);
                    if (!seenCallers.Add(callerFqn))
                        return FindExecution.Continue;

                    var node = BuildNode(callerFn, refNode, isCall, budget);
                    if (node == null)
                        return FindExecution.Continue;

                    callerEntries.Add((callerFn, node));
                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());

            foreach (var (callerFn, node) in callerEntries)
            {
                if (budget.Exhausted) break;
                node["children"] = BuildIncoming(callerFn, depth + 1, maxDepth, visited, budget);
                children.Add(node);
            }

            // Allow this function to be re-expanded along a different branch of the tree.
            visited.Remove(fqn);

            return children;
        }

        // ---- Outgoing (callees) -------------------------------------------------

        private List<object> BuildOutgoing(
            ICSharpFunctionDeclaration declaration, int depth, int maxDepth, HashSet<string> visited, NodeBudgetCounter budget)
        {
            var children = new List<object>();
            if (depth >= maxDepth || budget.Exhausted)
                return children;

            var declElement = declaration.DeclaredElement;
            var fqn = declElement != null ? PsiHelpers.GetQualifiedName(declElement) : null;
            if (fqn != null && !visited.Add(fqn))
                return children;

            var body = declaration.Body;
            if (body == null)
            {
                if (fqn != null) visited.Remove(fqn);
                return children;
            }

            // Dedup callees by qualified name so the same callee appears once per parent.
            var seenCallees = new HashSet<string>();
            var calleeEntries = new List<(IFunction calleeFn, Dictionary<string, object> node)>();

            foreach (var invocation in body.Descendants<IInvocationExpression>())
            {
                if (budget.Exhausted) break;

                var reference = invocation.InvokedExpression as IReferenceExpression;
                if (reference == null) continue;

                var callee = reference.Reference.Resolve().DeclaredElement as IFunction;
                if (callee == null) continue;

                var calleeFqn = PsiHelpers.GetQualifiedName(callee);
                if (!seenCallees.Add(calleeFqn))
                    continue;

                var node = BuildNode(callee, invocation, true, budget);
                if (node == null) continue;

                calleeEntries.Add((callee, node));
            }

            foreach (var (calleeFn, node) in calleeEntries)
            {
                if (budget.Exhausted) break;

                // Recurse into the callee's C# declaration, if we can find one.
                ICSharpFunctionDeclaration calleeDecl = null;
                foreach (var d in calleeFn.GetDeclarations())
                {
                    if (d is ICSharpFunctionDeclaration f)
                    {
                        calleeDecl = f;
                        break;
                    }
                }

                node["children"] = calleeDecl != null
                    ? BuildOutgoing(calleeDecl, depth + 1, maxDepth, visited, budget)
                    : new List<object>();

                children.Add(node);
            }

            if (fqn != null) visited.Remove(fqn);

            return children;
        }

        // ---- Node construction --------------------------------------------------

        /// <summary>
        /// Builds a call-hierarchy node for <paramref name="element"/>, locating it at
        /// the position of <paramref name="siteNode"/> (the call/reference site). Charges
        /// one unit against the node budget.
        /// </summary>
        private static Dictionary<string, object> BuildNode(
            IDeclaredElement element, ITreeNode siteNode, bool isCall, NodeBudgetCounter budget)
        {
            if (element == null) return null;
            if (!budget.TryConsume()) return null;

            var node = new Dictionary<string, object>
            {
                ["method"] = element.ShortName,
                ["qualifiedName"] = PsiHelpers.GetQualifiedName(element),
                ["kind"] = isCall ? "call" : "reference"
            };

            if (siteNode != null)
            {
                var sourceFile = siteNode.GetSourceFile();
                var range = TreeNodeExtensions.GetDocumentRange(siteNode);
                if (sourceFile != null && range.IsValid())
                {
                    var (line, column) = PsiHelpers.GetLineColumn(range.StartOffset);
                    node["file"] = sourceFile.GetLocation().FullPath;
                    node["line"] = line;
                    node["column"] = column;
                }
            }

            node["children"] = new List<object>();
            return node;
        }

        /// <summary>
        /// Tracks the global node budget across the whole recursion to bound work.
        /// </summary>
        private class NodeBudgetCounter
        {
            private int _remaining = NodeBudget;
            public bool Truncated { get; private set; }

            public bool Exhausted => _remaining <= 0;

            public bool TryConsume()
            {
                if (_remaining <= 0)
                {
                    Truncated = true;
                    return false;
                }
                _remaining--;
                return true;
            }
        }
    }
}
