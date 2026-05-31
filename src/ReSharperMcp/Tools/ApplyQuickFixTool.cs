using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.TextControl;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// RISKY / best-effort tool. Applies a ReSharper quick-fix (bulb action) at a position.
    ///
    /// Executing a quick-fix headlessly genuinely needs a UI/main thread and an
    /// <see cref="ITextControl"/>. Implements <see cref="IMcpSelfTransactingWriteTool"/> so the
    /// component dispatches it onto the R# main thread under a write lock WITHOUT an outer
    /// auto-committing PSI transaction: <see cref="IBulbAction.Execute"/> (via BulbActionExecutor)
    /// manages its own transactions, so we must not wrap it in one. The tool drives the ReSharper
    /// daemon over the file headlessly (via <see cref="DaemonHighlightingCollector"/>) to produce the
    /// highlightings, maps the ones covering the position to quick-fixes via <see cref="QuickFixTable"/>,
    /// and executes the chosen bulb action against a text control.
    ///
    /// Everything is wrapped defensively: if no fix is requested it lists the available ones;
    /// if a text control cannot be obtained or the action throws, it returns a graceful
    /// <c>{ applied:false, reason:... }</c> rather than hanging or crashing the host.
    /// </summary>
    public class ApplyQuickFixTool : IMcpSelfTransactingWriteTool
    {
        private readonly ISolution _solution;

        public ApplyQuickFixTool(ISolution solution) => _solution = solution;

        public string Name => "apply_quick_fix";

        public string Description =>
            "Apply a ReSharper quick-fix (bulb action) at a position in a file. " +
            "RISKY / best-effort: quick-fix execution needs an interactive editor, so it may not " +
            "succeed headlessly. Provide filePath + line + column (1-based). " +
            "Omit 'fixId'/'index' to list the available fixes at that position. " +
            "If exactly one fix is available it is applied automatically; otherwise pass 'fixId' " +
            "(the fix's display text, from the listing) or 'index' (0-based) to choose one. " +
            "Returns { applied, fixId, file, reason?, changedFiles? }.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new { type = "string", description = "Absolute path to the file containing the issue to fix" },
                line = new { type = "integer", description = "1-based line number of the issue" },
                column = new { type = "integer", description = "1-based column number of the issue" },
                fixId = new { type = "string", description = "The display text of the fix to apply (as returned in the 'available' list). Optional." },
                index = new { type = "integer", description = "0-based index into the available fixes to apply. Optional alternative to fixId." }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            // ----- input -----
            var filePath = arguments["filePath"]?.ToString();
            var line = arguments["line"]?.Value<int>() ?? 0;
            var column = arguments["column"]?.Value<int>() ?? 0;
            var fixId = arguments["fixId"]?.ToString();
            var hasIndex = arguments["index"] != null && arguments["index"].Type != JTokenType.Null;
            var index = arguments["index"]?.Value<int>() ?? -1;

            if (string.IsNullOrEmpty(filePath) || line <= 0 || column <= 0)
                return new { error = "Provide 'filePath' + 'line' + 'column' (1-based)" };

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            var document = sourceFile.Document;
            if (document == null)
                return new { error = "Could not get document for file" };

            // ----- compute the offset for the position -----
            int offset;
            try
            {
                var docLine = (Int32<DocLine>)(line - 1);
                var docColumn = (Int32<DocColumn>)(column - 1);
                offset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
            }
            catch (Exception ex)
            {
                return new { error = $"Could not resolve position {line}:{column}: {ex.Message}" };
            }

            // ----- gather candidate fixes -----
            List<Candidate> candidates;
            try
            {
                candidates = CollectCandidates(sourceFile, document, offset);
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to collect quick-fixes: {ex.Message}" };
            }

            if (candidates.Count == 0)
            {
                return new
                {
                    applied = false,
                    file = filePath,
                    reason = "No quick-fixes are available at this position. The file may not have " +
                             "been analyzed yet (open it in the editor so the daemon computes highlightings), " +
                             "or there is no issue here.",
                    available = new object[0]
                };
            }

            var available = candidates
                .Select((c, i) => new { index = i, fixId = c.Text, highlighting = c.HighlightingId })
                .ToList<object>();

            // ----- choose which fix to apply -----
            Candidate chosen = null;

            if (!string.IsNullOrEmpty(fixId))
            {
                chosen = candidates.FirstOrDefault(c =>
                    string.Equals(c.Text, fixId, StringComparison.Ordinal))
                    ?? candidates.FirstOrDefault(c =>
                        string.Equals(c.Text, fixId, StringComparison.OrdinalIgnoreCase));

                if (chosen == null)
                    return new
                    {
                        applied = false,
                        file = filePath,
                        reason = $"No available fix matches fixId '{fixId}'.",
                        available
                    };
            }
            else if (hasIndex)
            {
                if (index < 0 || index >= candidates.Count)
                    return new
                    {
                        applied = false,
                        file = filePath,
                        reason = $"index {index} is out of range (0..{candidates.Count - 1}).",
                        available
                    };
                chosen = candidates[index];
            }
            else if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                // Multiple fixes and no selection -> return the list so the caller can pick.
                return new
                {
                    applied = false,
                    file = filePath,
                    reason = $"{candidates.Count} fixes available; specify 'fixId' or 'index' to apply one.",
                    available
                };
            }

            // ----- apply the chosen fix -----
            return ApplyFix(chosen, document, sourceFile, filePath, available);
        }

        /// <summary>
        /// Collects the quick-fixes (bulb actions) available at the position. Drives the real ReSharper
        /// daemon engine headlessly over the file via <see cref="DaemonHighlightingCollector"/> so the
        /// highlightings exist even when the file was never opened in an editor (the document-markup model
        /// is empty in that case). Each covering <see cref="HighlightingInfo"/> is mapped to its available
        /// quick-fixes via <see cref="QuickFixTable"/>; one candidate is produced per bulb action.
        /// </summary>
        private List<Candidate> CollectCandidates(IPsiSourceFile sourceFile, IDocument document, int offset)
        {
            var result = new List<Candidate>();
            var seen = new HashSet<string>();

            var quickFixTable = _solution.GetComponent<QuickFixTable>();
            if (quickFixTable == null)
                return result;

            // Run the daemon orchestration headlessly. We are on the R# main thread inside a write lock
            // (IMcpSelfTransactingWriteTool), which also satisfies the read access the daemon
            // asserts, so the stages run and emit highlightings.
            var collected = DaemonHighlightingCollector.Collect(_solution, sourceFile);

            foreach (var highlightingInfo in collected)
            {
                if (highlightingInfo?.Highlighting == null) continue;

                // Keep only highlightings whose range covers the requested offset.
                var range = highlightingInfo.Range;
                if (!range.IsValid()) continue;
                if (offset < range.StartOffset.Offset || offset > range.EndOffset.Offset) continue;

                IEnumerable<QuickFixInstance> fixInstances;
                try
                {
                    fixInstances = quickFixTable.EnumerateAvailableQuickFixes(highlightingInfo);
                }
                catch
                {
                    continue;
                }

                if (fixInstances == null) continue;

                var highlightingId = highlightingInfo.Highlighting.GetType().Name;

                foreach (var fixInstance in fixInstances)
                {
                    if (fixInstance == null) continue;

                    IReadOnlyList<IntentionActionInstance> actionInstances;
                    try
                    {
                        actionInstances = fixInstance.CreateActionInstances(_solution);
                    }
                    catch
                    {
                        continue;
                    }

                    if (actionInstances == null) continue;

                    foreach (var actionInstance in actionInstances)
                    {
                        var bulbAction = actionInstance?.BulbAction;
                        if (bulbAction == null) continue;

                        var text = SafeText(actionInstance, bulbAction);
                        if (string.IsNullOrEmpty(text)) continue;

                        // De-dup identical fixes coming from overlapping highlightings.
                        var key = highlightingId + "|" + text;
                        if (!seen.Add(key)) continue;

                        result.Add(new Candidate
                        {
                            Text = text,
                            HighlightingId = highlightingId,
                            BulbAction = bulbAction
                        });
                    }
                }
            }

            return result;
        }

        private object ApplyFix(
            Candidate chosen,
            IDocument document,
            IPsiSourceFile sourceFile,
            string filePath,
            List<object> available)
        {
            // Reuse an already-open text control for this document if there is one; otherwise
            // create a synthetic, short-lived control. Either way we never block: a failure to
            // obtain a control results in a graceful "requires interactive editor" response.
            ITextControl textControl = null;
            LifetimeDefinition syntheticLifetime = null;
            var usedSyntheticControl = false;

            try
            {
                textControl = TryGetOpenTextControl(document);

                if (textControl == null)
                {
                    try
                    {
                        var tcManager = _solution.GetComponent<ITextControlManager>();
                        if (tcManager != null)
                        {
                            syntheticLifetime = Lifetime.Define(_solution.GetLifetime(), "ReSharperMcp.ApplyQuickFix");
                            textControl = tcManager.CreateTextControl(syntheticLifetime.Lifetime, document);
                            usedSyntheticControl = textControl != null;
                        }
                    }
                    catch (Exception ex)
                    {
                        return new
                        {
                            applied = false,
                            fixId = chosen.Text,
                            file = filePath,
                            reason = "requires interactive editor / not supported headless: " +
                                     $"could not create a text control ({ex.Message})",
                            available
                        };
                    }
                }

                if (textControl == null)
                    return new
                    {
                        applied = false,
                        fixId = chosen.Text,
                        file = filePath,
                        reason = "requires interactive editor / not supported headless: no text control available",
                        available
                    };

                // Snapshot the document text so we can report whether the file actually changed.
                var before = SafeGetText(document);

                try
                {
                    // We are already on the main thread inside a write lock (no outer transaction:
                    // IMcpSelfTransactingWriteTool). The bulb action manages its own transaction, so
                    // invoke it directly.
                    chosen.BulbAction.Execute(_solution, textControl);
                }
                catch (Exception ex)
                {
                    return new
                    {
                        applied = false,
                        fixId = chosen.Text,
                        file = filePath,
                        reason = "requires interactive editor / not supported headless: " +
                                 $"the fix could not run ({ex.GetType().Name}: {ex.Message})",
                        available
                    };
                }

                var after = SafeGetText(document);
                var changedFiles = new List<string>();
                if (before == null || after == null || !string.Equals(before, after, StringComparison.Ordinal))
                    changedFiles.Add(filePath);

                return new
                {
                    applied = true,
                    fixId = chosen.Text,
                    file = filePath,
                    usedSyntheticEditor = usedSyntheticControl,
                    changedFiles
                };
            }
            finally
            {
                // Always tear down a synthetic control's lifetime; never leave it dangling.
                try { syntheticLifetime?.Terminate(); }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Finds an already-open text control whose document is the one we are fixing. Avoids
        /// opening editor tabs and is the safest source of a usable control.
        /// </summary>
        private ITextControl TryGetOpenTextControl(IDocument document)
        {
            try
            {
                var tcManager = _solution.GetComponent<ITextControlManager>();
                if (tcManager == null) return null;

                foreach (var tc in tcManager.TextControls)
                {
                    if (tc != null && ReferenceEquals(tc.Document, document))
                        return tc;
                }
            }
            catch
            {
                // ignore — fall back to synthetic control
            }

            return null;
        }

        private static string SafeText(IntentionActionInstance actionInstance, IBulbAction bulbAction)
        {
            try
            {
                var rich = actionInstance.RichText;
                if (rich != null)
                {
                    var t = rich.Text;
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch
            {
                // fall through to bulb action text
            }

            try
            {
                return bulbAction.Text;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetText(IDocument document)
        {
            try
            {
                return document.GetText();
            }
            catch
            {
                return null;
            }
        }

        private class Candidate
        {
            public string Text;
            public string HighlightingId;
            public IBulbAction BulbAction;
        }
    }
}
