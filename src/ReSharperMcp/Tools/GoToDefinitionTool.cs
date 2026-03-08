using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GoToDefinitionTool : IMcpTool
    {
        private readonly ISolution _solution;

        public GoToDefinitionTool(ISolution solution) => _solution = solution;

        public string Name => "go_to_definition";

        public string Description =>
            "Navigate to the definition/declaration of a symbol. " +
            "Given a usage site (file+line+column) or a symbol name, returns the file path and position " +
            "where the symbol is declared, along with the declaration source text.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to find the definition of (e.g. 'MyClass', 'Namespace.MyClass'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                maxTextLength = new { type = "integer", description = "Maximum length of the returned source text. Default: 200. Set higher (e.g. 2000) to get full declaration bodies." },
                filePath = new { type = "string", description = "Absolute path to the file containing a usage of the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol usage" },
                column = new { type = "integer", description = "1-based column number of the symbol usage" }
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

            var maxTextLength = arguments["maxTextLength"]?.Value<int>() ?? PsiHelpers.MaxSnippetLength;
            if (maxTextLength <= 0) maxTextLength = PsiHelpers.MaxSnippetLength;
            if (maxTextLength > 50000) maxTextLength = 50000;

            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count == 0)
                return new
                {
                    symbol = declaredElement.ShortName,
                    kind = declaredElement.GetElementType().PresentableName,
                    error = "Symbol has no source declarations (may be from a compiled assembly)"
                };

            var decl = declarations[0];
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid())
                return new { error = "Could not resolve declaration location" };

            var declSourceFile = decl.GetSourceFile();
            if (declSourceFile == null)
                return new { error = "Declaration source file not available" };

            var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);

            return new
            {
                symbol = declaredElement.ShortName,
                kind = declaredElement.GetElementType().PresentableName,
                file = declSourceFile.GetLocation().FullPath,
                line = declLine,
                column = declCol,
                text = PsiHelpers.TruncateSnippet(decl.GetText(), maxTextLength)
            };
        }
    }
}
