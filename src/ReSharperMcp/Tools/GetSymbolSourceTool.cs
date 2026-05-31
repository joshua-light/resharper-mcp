using System.Collections.Generic;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetSymbolSourceTool : IMcpTool
    {
        /// <summary>Maximum length of a single declaration's source body before truncation.</summary>
        private const int MaxSourceLength = 20000;

        private readonly ISolution _solution;

        public GetSymbolSourceTool(ISolution solution) => _solution = solution;

        public string Name => "get_symbol_source";

        public string Description =>
            "Get the FULL declaration source code of a symbol (class, method, property, etc.), " +
            "not just a short snippet. Provide either a symbolName (e.g. 'MyClass' or " +
            "'Namespace.MyClass') or a file path with position (line/column). " +
            "By default returns only the primary declaration; set allDeclarations=true to return " +
            "every partial declaration / overload. Large bodies are capped and flagged as truncated. " +
            "Library symbols without available source return location-only with source=null.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to get source for (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                allDeclarations = new { type = "boolean", description = "Return every declaration (partial classes, overloads, etc.) instead of only the primary one. Default: false." }
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

            var allDeclarations = arguments["allDeclarations"]?.Value<bool>() ?? false;

            var declarationResults = new List<object>();

            var declarations = declaredElement.GetDeclarations();
            if (declarations != null)
            {
                foreach (var decl in declarations)
                {
                    if (decl == null) continue;

                    var info = BuildDeclarationInfo(decl);
                    if (info == null) continue;

                    declarationResults.Add(info);

                    if (!allDeclarations) break;
                }
            }

            // Library symbol (or otherwise no source declaration available): return location-only.
            if (declarationResults.Count == 0)
            {
                declarationResults.Add(new Dictionary<string, object>
                {
                    ["file"] = null,
                    ["startLine"] = 0,
                    ["startColumn"] = 0,
                    ["endLine"] = 0,
                    ["endColumn"] = 0,
                    ["source"] = null,
                    ["truncated"] = false
                });
            }

            return new Dictionary<string, object>
            {
                ["symbol"] = declaredElement.ShortName,
                ["qualifiedName"] = PsiHelpers.GetQualifiedName(declaredElement),
                ["declarations"] = declarationResults
            };
        }

        private static Dictionary<string, object> BuildDeclarationInfo(IDeclaration decl)
        {
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid()) return null;

            var sourceFile = decl.GetSourceFile();
            var (startLine, startColumn) = PsiHelpers.GetLineColumn(range.StartOffset);
            var (endLine, endColumn) = PsiHelpers.GetLineColumn(range.EndOffset);

            var source = range.Document.GetText(range.TextRange);
            var truncated = false;
            if (source != null && source.Length > MaxSourceLength)
            {
                source = source.Substring(0, MaxSourceLength) + "...";
                truncated = true;
            }

            return new Dictionary<string, object>
            {
                ["file"] = sourceFile?.GetLocation().FullPath,
                ["startLine"] = startLine,
                ["startColumn"] = startColumn,
                ["endLine"] = endLine,
                ["endColumn"] = endColumn,
                ["source"] = source,
                ["truncated"] = truncated
            };
        }
    }
}
