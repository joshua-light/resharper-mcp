using System.Collections.Generic;
using System.Linq;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class BrowseNamespaceTool : IMcpTool
    {
        private readonly ISolution _solution;

        public BrowseNamespaceTool(ISolution solution) => _solution = solution;

        public string Name => "browse_namespace";

        public string Description =>
            "Browse the namespace hierarchy. With no arguments, lists all top-level namespaces. " +
            "With a namespace name, lists its child namespaces and types. " +
            "Enables top-down exploration of the codebase structure.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                namespaceName = new
                {
                    type = "string",
                    description = "Namespace to browse (e.g. 'MyApp.Core'). Omit or leave empty to list top-level namespaces."
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var namespaceName = arguments["namespaceName"]?.ToString() ?? "";

            var psiServices = _solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);

            // Find the target namespace
            INamespace targetNs;
            if (string.IsNullOrEmpty(namespaceName))
            {
                targetNs = symbolScope.GlobalNamespace;
            }
            else
            {
                targetNs = FindNamespace(symbolScope, namespaceName);
                if (targetNs == null)
                    return new { error = $"Namespace not found: {namespaceName}" };
            }

            // Collect child namespaces
            var childNamespaces = new List<object>();
            foreach (var childNs in targetNs.GetNestedNamespaces(symbolScope))
            {
                var typeCount = childNs.GetNestedTypeElements(symbolScope).Count();
                childNamespaces.Add(new
                {
                    name = childNs.ShortName,
                    qualifiedName = childNs.QualifiedName,
                    typeCount
                });
            }

            // Collect types in this namespace
            var types = new List<object>();
            foreach (var typeElement in targetNs.GetNestedTypeElements(symbolScope))
            {
                // Only include types directly in this namespace (not nested types)
                if (typeElement.GetContainingType() != null) continue;

                var typeInfo = new Dictionary<string, object>
                {
                    ["name"] = typeElement.ShortName,
                    ["kind"] = typeElement.GetElementType().PresentableName,
                };

                // Get declaration location
                var declarations = typeElement.GetDeclarations();
                if (declarations.Count > 0)
                {
                    var decl = declarations[0];
                    var sf = decl.GetSourceFile();
                    if (sf != null)
                    {
                        typeInfo["file"] = sf.GetLocation().FullPath;
                        var range = TreeNodeExtensions.GetDocumentRange(decl);
                        if (range.IsValid())
                        {
                            var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);
                            typeInfo["line"] = line;
                        }
                    }
                }

                types.Add(typeInfo);
            }

            return new
            {
                namespaceName = string.IsNullOrEmpty(namespaceName) ? "(root)" : namespaceName,
                childNamespaces = childNamespaces.OrderBy(n => ((dynamic)n).name).ToList(),
                types = types.OrderBy(t => ((Dictionary<string, object>)t)["name"]).ToList()
            };
        }

        private static INamespace FindNamespace(ISymbolScope symbolScope, string qualifiedName)
        {
            var parts = qualifiedName.Split('.');
            var current = symbolScope.GlobalNamespace;

            foreach (var part in parts)
            {
                INamespace found = null;
                foreach (var child in current.GetNestedNamespaces(symbolScope))
                {
                    if (child.ShortName == part)
                    {
                        found = child;
                        break;
                    }
                }

                if (found == null) return null;
                current = found;
            }

            return current;
        }
    }
}
