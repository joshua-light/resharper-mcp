using System.Collections.Generic;
using System.Text;
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

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return new { error = resolved.Error };
            var sourceFile = resolved.SourceFile;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return new { error = "Could not get PSI tree for file" };

            var diagnostics = new List<DiagnosticEntry>();

            // Walk the tree looking for error elements and unresolved references
            foreach (var node in psiFile.Descendants())
            {
                // Error elements (syntax errors, etc.)
                if (node is IErrorElement errorElement)
                {
                    var range = TreeNodeExtensions.GetDocumentRange(node);
                    if (!range.IsValid()) continue;

                    var (errLine, errCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Severity = "error",
                        Message = errorElement.ErrorDescription,
                        Line = errLine,
                        Column = errCol,
                        Text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
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
                        diagnostics.Add(new DiagnosticEntry
                        {
                            Severity = resolveResult.ResolveErrorType == ResolveErrorType.DYNAMIC
                                ? "warning"
                                : "error",
                            Message = $"Cannot resolve symbol '{reference.GetName()}'",
                            Line = refLine,
                            Column = refCol,
                            Text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
                        });
                    }
                }
            }

            // Format compact output
            var sb = new StringBuilder();
            sb.Append(filePath).Append(" — ").Append(diagnostics.Count).AppendLine(" diagnostics");

            foreach (var d in diagnostics)
            {
                sb.AppendLine();
                sb.Append(d.Severity).Append(" :").Append(d.Line).Append(':').Append(d.Column);
                sb.Append(" — ").AppendLine(d.Message);
                if (d.Text != null)
                    sb.Append("  ").AppendLine(d.Text);
            }

            return sb.ToString().TrimEnd();
        }

        private class DiagnosticEntry
        {
            public string Severity;
            public string Message;
            public int Line;
            public int Column;
            public string Text;
        }
    }
}
