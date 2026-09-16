using System;
using System.Collections.Generic;
using System.Text;
using JetBrains.Application;
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
            "Returns error elements with their location and description. " +
            "Pass multiple files via the 'filePaths' array to check several files in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Absolute path to the file to check for errors"
                },
                filePaths = new
                {
                    type = "array",
                    description = "Array of absolute file paths to check for errors in batch. Results are concatenated with separators. Alternative to single 'filePath' parameter.",
                    items = new { type = "string" }
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var filePathsToken = arguments["filePaths"] as JArray;
            if (filePathsToken != null && filePathsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < filePathsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = new JObject();
                    itemArgs["filePath"] = filePathsToken[i]?.ToString();

                    sb.Append("=== [").Append(i + 1).Append('/').Append(filePathsToken.Count)
                      .Append("] ").Append(filePathsToken[i]).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteIsolated(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        /// <summary>
        ///   One broken file must not take the rest of the batch with it.
        /// </summary>
        private object ExecuteIsolated(JObject arguments)
        {
            try
            {
                return ExecuteSingle(arguments);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interruption.Current.CheckAndThrow();
                return new { error = $"ReSharper failed to inspect the file: {exception.GetType().Name}: {exception.Message}" };
            }
        }

        private object ExecuteSingle(JObject arguments)
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

            foreach (var node in psiFile.Descendants())
            {
                try
                {
                    Inspect(node, diagnostics);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // ReSharper can throw from deep inside its own analysis (observed: injected-PSI
                    // collection over a stale chameleon). One bad node must not lose the whole file.
                    Interruption.Current.CheckAndThrow();
                    Report(node, diagnostics, "warning", $"ReSharper failed to inspect this node ({exception.GetType().Name})");
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

        private static void Inspect(ITreeNode node, List<DiagnosticEntry> diagnostics)
        {
            // Syntax errors.
            if (node is IErrorElement errorElement)
                Report(node, diagnostics, "error", errorElement.ErrorDescription);

            // A reference inside a string literal is an injected language (regex, HTML, ...), never a
            // compile error, and asking for it triggers ReSharper's injected-PSI scan of the whole file.
            if (IsLiteral(node))
                return;

            // Unresolved references.
            foreach (var reference in node.GetReferences())
            {
                var errorType = reference.Resolve().ResolveErrorType;
                if (errorType == ResolveErrorType.OK || errorType == ResolveErrorType.IGNORABLE)
                    continue;

                var severity = errorType == ResolveErrorType.DYNAMIC ? "warning" : "error";
                Report(node, diagnostics, severity, $"Cannot resolve symbol '{reference.GetName()}'");
            }
        }

        private static bool IsLiteral(ITreeNode node) =>
            node is ILiteralExpression || node is ITokenNode token && token.GetTokenType().IsStringLiteral;

        private static void Report(ITreeNode node, List<DiagnosticEntry> diagnostics, string severity, string message)
        {
            var range = TreeNodeExtensions.GetDocumentRange(node);
            if (!range.IsValid())
                return;

            var (line, column) = PsiHelpers.GetLineColumn(range.StartOffset);
            diagnostics.Add(new DiagnosticEntry
            {
                Severity = severity,
                Message = message,
                Line = line,
                Column = column,
                Text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
            });
        }

        private static string ResultToString(object result)
        {
            if (result is string s) return s;
            var jo = JObject.FromObject(result);
            return "error: " + (jo["error"]?.ToString() ?? result.ToString());
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
