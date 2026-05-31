using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Runs the real ReSharper daemon inspection stages headlessly over a single file and reports
    /// the resulting highlightings (severity, inspection id, message, quick-fix availability).
    /// This is richer than <c>get_file_errors</c> (which only walks the PSI tree for syntax errors
    /// and unresolved references) because it surfaces actual code-analysis inspections.
    /// Read-only: it never mutates the PSI or documents.
    /// </summary>
    public class GetDiagnosticsTool : IMcpTool
    {
        private readonly ISolution _solution;

        public GetDiagnosticsTool(ISolution solution) => _solution = solution;

        public string Name => "get_diagnostics";

        public string Description =>
            "Run ReSharper's daemon inspections on a file and report diagnostics: severity, inspection id, " +
            "message, location, and whether a quick-fix is available. Richer than get_file_errors " +
            "(which only finds syntax errors and unresolved references). " +
            "Filter with minSeverity ('error'|'warning'|'suggestion'|'hint', default 'warning'). " +
            "Pass an optional position {line, column} to keep only the diagnostics covering that spot " +
            "(useful for explaining the error/warning under the cursor).";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Absolute path to the file to analyze"
                },
                minSeverity = new
                {
                    type = "string",
                    description = "Minimum severity to report: 'error', 'warning', 'suggestion', or 'hint'. Default: 'warning'."
                },
                position = new
                {
                    type = "object",
                    description = "Optional position. When set, only diagnostics whose range covers this offset are returned (explain-error mode).",
                    properties = new
                    {
                        line = new { type = "integer", description = "1-based line number" },
                        column = new { type = "integer", description = "1-based column number" }
                    }
                }
            },
            required = new[] { "filePath" }
        };

        public object Execute(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            var minSeverity = ParseSeverity(arguments["minSeverity"]?.ToString()) ?? Severity.WARNING;

            // Optional position (explain-error mode)
            int posLine = 0, posColumn = 0;
            var hasPosition = false;
            var positionToken = arguments["position"] as JObject;
            if (positionToken != null)
            {
                posLine = positionToken["line"]?.Value<int>() ?? 0;
                posColumn = positionToken["column"]?.Value<int>() ?? 0;
                hasPosition = posLine > 0 && posColumn > 0;
            }

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            // Resolve the document offset for position filtering, if requested.
            int? positionOffset = null;
            if (hasPosition)
            {
                var document = sourceFile.Document;
                if (document != null)
                {
                    var docLine = (Int32<DocLine>)(posLine - 1);
                    var docColumn = (Int32<DocColumn>)(posColumn - 1);
                    positionOffset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
                }
            }

            IContextBoundSettingsStore settings;
            HighlightingSettingsManager mgr;
            try
            {
                settings = sourceFile.GetSettingsStoreWithEditorConfig(_solution);
                mgr = _solution.GetComponent<HighlightingSettingsManager>();
            }
            catch (Exception e)
            {
                return new { error = $"Failed to initialize daemon settings/components: {e.Message}" };
            }

            // Drive the real ReSharper daemon engine headlessly over this file via the shared collector
            // (DaemonProcessBase.DoHighlighting orchestration). We are already on the R# main thread under a
            // read lock with documents committed (see McpServerComponent), which is what the daemon requires.
            var collected = DaemonHighlightingCollector.Collect(_solution, sourceFile, settings);

            // Shape the diagnostics.
            var diagnostics = new List<Dictionary<string, object>>();
            foreach (var info in collected)
            {
                var highlighting = info.Highlighting;
                if (highlighting == null) continue;

                // Severity
                Severity severity;
                try
                {
                    severity = mgr.GetSeverity(highlighting, sourceFile, _solution, settings);
                }
                catch
                {
                    continue;
                }

                if (!MeetsMinSeverity(severity, minSeverity)) continue;

                var range = info.Range;
                if (!range.IsValid()) continue;

                // Position filtering (explain-error mode): keep only diagnostics covering the offset.
                if (positionOffset.HasValue)
                {
                    var start = range.StartOffset.Offset;
                    var end = range.EndOffset.Offset;
                    if (positionOffset.Value < start || positionOffset.Value > end)
                        continue;
                }

                var (startLine, startCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                var (endLine, endCol) = PsiHelpers.GetLineColumn(range.EndOffset);

                string inspectionId = null;
                try
                {
                    inspectionId = highlighting.GetConfigurableSeverityId();
                }
                catch
                {
                    // Some highlightings have no configurable severity id.
                }

                // CouldHavePopupQuickFix(IHighlighting) is an unreliable false-negative headless: it reported
                // no fix for a NotAccessedField.Local warning that list_quick_fixes found 3 real fixes for.
                // Mirror list_quick_fixes exactly and ask the QuickFixTable to actually enumerate the fixes
                // available for this highlighting (HasQuickFix swallows errors and returns false on failure).
                bool hasQuickFix = DaemonHighlightingCollector.HasQuickFix(_solution, info);

                string message = null;
                try
                {
                    message = highlighting.ToolTip ?? highlighting.ErrorStripeToolTip;
                }
                catch
                {
                    // ToolTip can throw for some synthetic highlightings.
                }

                diagnostics.Add(new Dictionary<string, object>
                {
                    ["inspectionId"] = inspectionId ?? highlighting.GetType().Name,
                    ["severity"] = SeverityToString(severity),
                    ["message"] = message,
                    ["line"] = startLine,
                    ["column"] = startCol,
                    ["endLine"] = endLine,
                    ["endColumn"] = endCol,
                    ["hasQuickFix"] = hasQuickFix
                });
            }

            // Stable, predictable ordering: by location.
            var ordered = diagnostics
                .OrderBy(d => (int)d["line"])
                .ThenBy(d => (int)d["column"])
                .ToList();

            var result = new Dictionary<string, object>
            {
                ["file"] = filePath,
                ["diagnosticsCount"] = ordered.Count,
                ["diagnostics"] = ordered
            };

            // Runtime unknown: daemon stages may not emit highlightings headlessly. Be explicit.
            if (collected.Count == 0)
                result["note"] =
                    "No highlightings were produced by the daemon stages for this file. " +
                    "This can happen when inspection stages do not run headlessly outside an editor session. " +
                    "Try get_file_errors for syntax errors and unresolved references.";
            else if (ordered.Count == 0)
                result["note"] =
                    $"{collected.Count} highlighting(s) were produced but none met the minimum severity " +
                    $"'{SeverityToString(minSeverity)}'" +
                    (positionOffset.HasValue ? " (or covered the requested position)." : ".");

            return result;
        }

        private static Severity? ParseSeverity(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "error": return Severity.ERROR;
                case "warning": return Severity.WARNING;
                case "suggestion": return Severity.SUGGESTION;
                case "hint": return Severity.HINT;
                case "info": return Severity.INFO;
                default: return null;
            }
        }

        private static string SeverityToString(Severity severity)
        {
            switch (severity)
            {
                case Severity.ERROR: return "error";
                case Severity.WARNING: return "warning";
                case Severity.SUGGESTION: return "suggestion";
                case Severity.HINT: return "hint";
                case Severity.INFO: return "info";
                case Severity.DO_NOT_SHOW: return "none";
                default: return "unknown";
            }
        }

        /// <summary>
        /// The Severity enum is ordered ascending by importance (DO_NOT_SHOW &lt; INFO &lt; HINT &lt;
        /// SUGGESTION &lt; WARNING &lt; ERROR), so a numeric comparison suffices.
        /// </summary>
        private static bool MeetsMinSeverity(Severity severity, Severity minSeverity)
        {
            return (int)severity >= (int)minSeverity;
        }
    }
}
