using System.Collections.Generic;
using System.Text;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCleanup;
using JetBrains.ReSharper.Features.Altering.CodeCleanup;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CodeStyle;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.Util;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Implements IMcpSelfTransactingWriteTool because cleanup/style modes use CodeCleanupRunner
    /// which manages its own PSI transactions. Format mode creates its own PsiTransaction inline.
    /// </summary>
    public class FormatFileTool : IMcpSelfTransactingWriteTool
    {
        private readonly ISolution _solution;
        private readonly CodeCleanupSettingsComponent _cleanupSettings;

        public FormatFileTool(ISolution solution, CodeCleanupSettingsComponent cleanupSettings)
        {
            _solution = solution;
            _cleanupSettings = cleanupSettings;
        }

        public string Name => "format_file";

        public string Description =>
            "Format a source file or run full code cleanup. " +
            "mode='format' (default): applies indentation, spacing, line breaks. " +
            "mode='cleanup': runs full code cleanup — formatting plus code style fixes " +
            "(remove redundant qualifiers, sort/remove usings, apply var preferences, naming conventions, etc.). " +
            "mode='style': applies only code style fixes without reformatting. " +
            "Pass multiple files via the 'filePaths' array to format several files in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Absolute path to the file to format/cleanup"
                },
                filePaths = new
                {
                    type = "array",
                    description = "Array of absolute file paths to format/cleanup in batch. Results are concatenated with separators. Alternative to single 'filePath' parameter.",
                    items = new { type = "string" }
                },
                mode = new
                {
                    type = "string",
                    description = "Operation mode: 'format' (whitespace only, default), 'cleanup' (full cleanup including formatting and code style), 'style' (code style fixes only, no reformatting)",
                    @enum = new[] { "format", "cleanup", "style" }
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
                    CopyIfPresent(arguments, itemArgs, "mode");

                    sb.Append("=== [").Append(i + 1).Append('/').Append(filePathsToken.Count)
                      .Append("] ").Append(filePathsToken[i]).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            var mode = arguments["mode"]?.ToString() ?? "format";

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return new { error = resolved.Error };
            var sourceFile = resolved.SourceFile;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return new { error = "Could not get PSI tree for file" };

            if (mode == "format")
            {
                var language = psiFile.Language;
                var formatter = language.LanguageService()?.CodeFormatter;
                if (formatter == null)
                    return new { error = $"No code formatter available for language: {language.Name}" };

                // Format mode modifies the PSI tree directly — needs its own PsiTransaction
                using (PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(
                    _solution.GetPsiServices(), "ReSharperMcp.format_file"))
                {
                    formatter.FormatFile(psiFile, CodeFormatProfile.DEFAULT, null);
                }

                return $"{filePath} — formatted successfully";
            }

            // cleanup or style mode — CodeCleanupRunner manages its own transactions
            CodeCleanupService.DefaultProfileType profileType;
            switch (mode)
            {
                case "cleanup":
                    profileType = CodeCleanupService.DefaultProfileType.FULL;
                    break;
                case "style":
                    profileType = CodeCleanupService.DefaultProfileType.CODE_STYLE;
                    break;
                default:
                    return new { error = $"Unknown mode: {mode}. Use 'format', 'cleanup', or 'style'." };
            }

            var profile = _cleanupSettings.GetDefaultProfile(profileType);
            if (profile == null)
                return new { error = $"No default cleanup profile found for mode: {mode}" };

            var filesProvider = new SingleFileCleanupProvider(_solution, resolved.ProjectFile, sourceFile);
            CodeCleanupRunner.CleanupFiles(filesProvider, profile, true);

            return $"{filePath} — {mode} completed successfully";
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

        private class SingleFileCleanupProvider : ICodeCleanupFilesProvider
        {
            private readonly ISolution _solution;
            private readonly IProjectFile _projectFile;
            private readonly IPsiSourceFile _sourceFile;

            public SingleFileCleanupProvider(ISolution solution, IProjectFile projectFile, IPsiSourceFile sourceFile)
            {
                _solution = solution;
                _projectFile = projectFile;
                _sourceFile = sourceFile;
            }

            public ISolution Solution => _solution;
            public IProjectItem ProjectItem => _projectFile;

            public IReadOnlyList<IPsiSourceFile> GetFiles()
            {
                return new[] { _sourceFile };
            }

            public DocumentRange[] GetRangesForFile(IPsiSourceFile file)
            {
                var document = file.Document;
                if (document == null)
                    return null;

                var range = new DocumentRange(document, new TextRange(0, document.GetTextLength()));
                return new[] { range };
            }

            public bool IsSuitableProjectElement(IProjectModelElement element)
            {
                return true;
            }

            public bool IsSuitableFile(IProjectFile file)
            {
                return file.Location.FullPath == _projectFile.Location.FullPath;
            }
        }
    }
}
