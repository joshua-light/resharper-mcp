using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

            // Format compact output
            var sb = new StringBuilder();
            var displayName = string.IsNullOrEmpty(namespaceName) ? "(root)" : namespaceName;
            sb.Append("namespace: ").AppendLine(displayName);

            // Child namespaces
            var childNamespaces = targetNs.GetNestedNamespaces(symbolScope)
                .OrderBy(ns => ns.ShortName)
                .ToList();

            if (childNamespaces.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("child namespaces:");
                foreach (var childNs in childNamespaces)
                {
                    var typeCount = childNs.GetNestedTypeElements(symbolScope).Count();
                    sb.Append("  ").Append(childNs.QualifiedName);
                    sb.Append(" (").Append(typeCount).AppendLine(" types)");
                }
            }

            // Types in this namespace
            var types = targetNs.GetNestedTypeElements(symbolScope)
                .Where(te => te.GetContainingType() == null) // Only direct types, not nested
                .OrderBy(te => te.ShortName)
                .ToList();

            if (types.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("types:");
                foreach (var typeElement in types)
                {
                    sb.Append("  ").Append(typeElement.GetElementType().PresentableName);
                    sb.Append(' ').Append(typeElement.ShortName);

                    var declarations = typeElement.GetDeclarations();
                    if (declarations.Count > 0)
                    {
                        var decl = declarations[0];
                        var sf = decl.GetSourceFile();
                        if (sf != null)
                        {
                            var filePath = sf.GetLocation().FullPath;
                            var fileName = Path.GetFileName(filePath);

                            if (IsGeneratedFile(fileName))
                                sb.Append(" [generated]");

                            sb.Append(" — ").Append(fileName);

                            var range = TreeNodeExtensions.GetDocumentRange(decl);
                            if (range.IsValid())
                            {
                                var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);
                                sb.Append(':').Append(line);
                            }
                        }
                    }

                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsGeneratedFile(string fileName)
        {
            return fileName.EndsWith(".g.cs") ||
                   fileName.EndsWith(".g.fs") ||
                   fileName.EndsWith(".generated.cs") ||
                   fileName.EndsWith(".designer.cs", System.StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".g.i.cs") ||
                   fileName == "AssemblyInfo.cs" ||
                   fileName == "AssemblyAttributes.cs" ||
                   fileName.EndsWith(".razor.g.cs");
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
