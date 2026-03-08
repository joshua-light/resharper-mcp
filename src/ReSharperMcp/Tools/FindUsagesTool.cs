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
            "in the current solution. Provide the file path and position (line/column) of the symbol.";

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

            return new
            {
                symbol = declaredElement.ShortName,
                kind = declaredElement.GetElementType().PresentableName,
                declarationFile = filePath,
                declarationLine = line,
                declarationColumn = column,
                usagesCount = usages.Count,
                usages
            };
        }
    }
}
