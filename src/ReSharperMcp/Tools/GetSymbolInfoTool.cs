using System.Collections.Generic;
using System.Linq;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetSymbolInfoTool : IMcpTool
    {
        private readonly ISolution _solution;

        public GetSymbolInfoTool(ISolution solution) => _solution = solution;

        public string Name => "get_symbol_info";

        public string Description =>
            "Get detailed information about a code symbol at a given position: " +
            "kind, full qualified name, type, documentation, containing type/namespace, and parameter info for methods.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" }
            },
            required = new[] { "filePath", "line", "column" }
        };

        public object Execute(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            var line = arguments["line"]?.Value<int>() ?? 0;
            var column = arguments["column"]?.Value<int>() ?? 0;

            if (string.IsNullOrEmpty(filePath) || line <= 0 || column <= 0)
                return new { error = "filePath, line, and column are required (1-based)" };

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            var node = PsiHelpers.GetNodeAtPosition(sourceFile, line, column);
            if (node == null)
                return new { error = $"No syntax node found at {line}:{column}" };

            var declaredElement = PsiHelpers.GetDeclaredElement(node);
            if (declaredElement == null)
                return new { error = $"No resolvable symbol found at {line}:{column}" };

            var lang = declaredElement.PresentationLanguage ?? CSharpLanguage.Instance;

            var result = new Dictionary<string, object>
            {
                ["name"] = declaredElement.ShortName,
                ["kind"] = declaredElement.GetElementType().PresentableName,
            };

            // Containing namespace/type
            if (declaredElement is IClrDeclaredElement clrElement)
            {
                var containingType = clrElement.GetContainingType();
                if (containingType != null)
                {
                    result["containingType"] = containingType.GetClrName().FullName;
                    var ns = containingType.GetContainingNamespace();
                    if (ns != null)
                        result["namespace"] = ns.QualifiedName;
                }

                if (declaredElement is ITypeElement typeElem)
                {
                    var ns = typeElem.GetContainingNamespace();
                    if (ns != null)
                        result["namespace"] = ns.QualifiedName;
                }
            }

            // Type information for typed members
            if (declaredElement is ITypeOwner typeOwner)
                result["type"] = typeOwner.Type.GetPresentableName(lang);

            // Method-specific info
            if (declaredElement is IParametersOwner parametersOwner)
            {
                var parameters = parametersOwner.Parameters.Select(p => new
                {
                    name = p.ShortName,
                    type = p.Type.GetPresentableName(lang),
                    isOptional = p.IsOptional
                }).ToList();

                result["parameters"] = parameters;

                if (declaredElement is IMethod method)
                {
                    result["returnType"] = method.ReturnType.GetPresentableName(lang);
                    result["isStatic"] = method.IsStatic;
                    result["isAbstract"] = method.IsAbstract;
                    result["isVirtual"] = method.IsVirtual;
                    result["isOverride"] = method.IsOverride;
                }
            }

            // Type-specific info
            if (declaredElement is ITypeElement typeElement)
            {
                if (typeElement is IModifiersOwner modOwner)
                    result["isAbstract"] = modOwner.IsAbstract;

                var superTypes = typeElement.GetSuperTypes()
                    .Select(t => t.GetPresentableName(lang))
                    .ToList();
                if (superTypes.Any())
                    result["baseTypes"] = superTypes;
            }

            // XML documentation
            var xmlDoc = declaredElement.GetXMLDoc(true);
            if (xmlDoc != null)
            {
                var summary = xmlDoc.SelectSingleNode("//summary")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(summary))
                    result["documentation"] = summary;

                var remarks = xmlDoc.SelectSingleNode("//remarks")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(remarks))
                    result["remarks"] = remarks;
            }

            // Declaration location
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var range = TreeNodeExtensions.GetDocumentRange(decl);
                if (range.IsValid())
                {
                    var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                    result["declarationFile"] = decl.GetSourceFile()?.GetLocation().FullPath;
                    result["declarationLine"] = declLine;
                    result["declarationColumn"] = declCol;
                }
            }

            return result;
        }
    }
}
