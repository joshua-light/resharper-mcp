using System.Collections.Generic;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetFileErrorsTool : IMcpTool
    {
        private readonly ISolution _solution;

        public GetFileErrorsTool(ISolution solution) => _solution = solution;

        public string Name => "get_file_errors";

        public string Description =>
            "Get compile errors and unresolved references in a file by walking the PSI tree. " +
            "Returns error elements with their location and description.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Absolute path to the file to check for errors"
                }
            },
            required = new[] { "filePath" }
        };

        public object Execute(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();

            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return new { error = "Could not get PSI tree for file" };

            var diagnostics = new List<object>();

            // Walk the tree looking for error elements and unresolved references
            foreach (var node in psiFile.Descendants())
            {
                // Error elements (syntax errors, etc.)
                if (node is IErrorElement errorElement)
                {
                    var range = TreeNodeExtensions.GetDocumentRange(node);
                    if (!range.IsValid()) continue;

                    var (errLine, errCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                    diagnostics.Add(new
                    {
                        severity = "error",
                        message = errorElement.ErrorDescription,
                        line = errLine,
                        column = errCol,
                        text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
                    });
                }

                // Check for unresolved references
                foreach (var reference in node.GetReferences())
                {
                    var resolveResult = reference.Resolve();
                    if (resolveResult.ResolveErrorType != ResolveErrorType.OK &&
                        resolveResult.ResolveErrorType != ResolveErrorType.IGNORABLE)
                    {
                        var refRange = TreeNodeExtensions.GetDocumentRange(node);
                        if (!refRange.IsValid()) continue;

                        var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                        diagnostics.Add(new
                        {
                            severity = resolveResult.ResolveErrorType == ResolveErrorType.DYNAMIC
                                ? "warning"
                                : "error",
                            message = $"Cannot resolve symbol '{reference.GetName()}'",
                            line = refLine,
                            column = refCol,
                            text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
                        });
                    }
                }
            }

            return new
            {
                file = filePath,
                diagnosticsCount = diagnostics.Count,
                diagnostics
            };
        }
    }
}
