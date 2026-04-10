using System.Text;
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
            "where the symbol is declared, along with the declaration source text. " +
            "Pass multiple symbols via the 'symbols' array to navigate to several in one call.";

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
                column = new { type = "integer", description = "1-based column number of the symbol usage" },
                symbols = new
                {
                    type = "array",
                    description = "Array of symbols to navigate to in batch. Each item is an object with symbolName/kind or filePath/line/column. Results are concatenated with separators.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Symbol name" },
                            kind = new { type = "string", description = "Symbol kind filter" },
                            filePath = new { type = "string", description = "File path" },
                            line = new { type = "integer", description = "1-based line" },
                            column = new { type = "integer", description = "1-based column" }
                        }
                    }
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var symbolsToken = arguments["symbols"] as JArray;
            if (symbolsToken != null && symbolsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < symbolsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = CloneObject(symbolsToken[i] as JObject);
                    CopyIfPresent(arguments, itemArgs, "maxTextLength");

                    var label = itemArgs["symbolName"]?.ToString() ??
                                $"{itemArgs["filePath"]}:{itemArgs["line"]}:{itemArgs["column"]}";
                    sb.Append("=== [").Append(i + 1).Append('/').Append(symbolsToken.Count)
                      .Append("] ").Append(label).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
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
                    error = $"{declaredElement.GetElementType().PresentableName} {declaredElement.ShortName}: " +
                            "no source declarations (may be from a compiled assembly)"
                };

            var decl = declarations[0];
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid())
                return new { error = "Could not resolve declaration location" };

            var declSourceFile = decl.GetSourceFile();
            if (declSourceFile == null)
                return new { error = "Declaration source file not available" };

            var declFilePath = declSourceFile.GetLocation().FullPath;
            if (string.IsNullOrEmpty(declFilePath))
                declFilePath = "[no source]";

            var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);

            var sb = new StringBuilder();
            sb.Append(PsiHelpers.FormatSignature(declaredElement));
            sb.Append(" — ").Append(declFilePath);
            sb.Append(':').Append(declLine).Append(':').AppendLine(declCol.ToString());
            sb.Append(PsiHelpers.TruncateSnippet(decl.GetText(), maxTextLength));

            return sb.ToString().TrimEnd();
        }

        private static JObject CloneObject(JObject source)
        {
            if (source == null) return new JObject();
            return (JObject)source.DeepClone();
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
    }
}
