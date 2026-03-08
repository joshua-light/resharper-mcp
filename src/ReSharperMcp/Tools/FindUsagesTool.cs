using System.Collections.Generic;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class FindUsagesTool : IMcpTool
    {
        private readonly ISolution _solution;

        public FindUsagesTool(ISolution solution) => _solution = solution;

        public string Name => "find_usages";

        public string Description =>
            "Find all usages/references of a code symbol (class, method, property, variable, etc.) " +
            "in the current solution. Provide either a symbolName (e.g. 'MyClass' or 'Namespace.MyClass') " +
            "or a file path with position (line/column).";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to find usages of (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            var usages = new List<object>();
            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            psiServices.Finder.FindReferences(
                declaredElement,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (findResult is FindResultReference reference)
                    {
                        var refNode = reference.Reference.GetTreeNode();
                        var refSourceFile = refNode.GetSourceFile();
                        if (refSourceFile != null)
                        {
                            var refRange = TreeNodeExtensions.GetDocumentRange(refNode);
                            if (refRange.IsValid())
                            {
                                var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                                usages.Add(new
                                {
                                    file = refSourceFile.GetLocation().FullPath,
                                    line = refLine,
                                    column = refCol,
                                    text = PsiHelpers.TruncateSnippet(
                                        refNode.Parent?.GetText() ?? refNode.GetText())
                                });
                            }
                        }
                    }
                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());

            var result = new Dictionary<string, object>
            {
                ["symbol"] = declaredElement.ShortName,
                ["kind"] = declaredElement.GetElementType().PresentableName,
                ["qualifiedName"] = PsiHelpers.GetQualifiedName(declaredElement),
                ["usagesCount"] = usages.Count,
                ["usages"] = usages
            };

            // Include declaration location if available
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var declRange = TreeNodeExtensions.GetDocumentRange(decl);
                if (declRange.IsValid())
                {
                    var declSourceFile = decl.GetSourceFile();
                    if (declSourceFile != null)
                    {
                        var (declLine, declCol) = PsiHelpers.GetLineColumn(declRange.StartOffset);
                        result["declarationFile"] = declSourceFile.GetLocation().FullPath;
                        result["declarationLine"] = declLine;
                        result["declarationColumn"] = declCol;
                    }
                }
            }

            return result;
        }
    }
}
