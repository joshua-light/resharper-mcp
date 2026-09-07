using System;
using System.Collections.Generic;
using JetBrains.Application;
using JetBrains.Application.Settings;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Dependencies;

namespace ReSharperMcp
{
    /// <summary>
    ///   Collects daemon highlightings under an interruptible background read action.
    /// </summary>
    /// <remarks>
    ///   Callers must have committed documents and read access, and must not hold a write lock.
    /// </remarks>
    public static class DaemonHighlightingCollector
    {
        /// <summary>
        /// Runs the real ReSharper daemon inspection stages over a single source file (synchronously,
        /// in-process, headlessly) and returns every collected <see cref="HighlightingInfo"/>.
        /// Returns an empty list (never null) if the daemon could not be driven for this file.
        /// </summary>
        /// <param name="solution">The current solution (used to resolve daemon components).</param>
        /// <param name="sourceFile">The file to analyze.</param>
        /// <param name="settings">
        /// The settings store to drive the daemon with. When null, the per-file editor-config-aware store is
        /// resolved via <c>sourceFile.GetSettingsStoreWithEditorConfig(solution)</c>.
        /// </param>
        public static IList<HighlightingInfo> Collect(
            ISolution solution,
            IPsiSourceFile sourceFile,
            IContextBoundSettingsStore settings = null)
        {
            if (solution == null || sourceFile == null)
                return new List<HighlightingInfo>();

            if (settings == null)
            {
                try
                {
                    settings = sourceFile.GetSettingsStoreWithEditorConfig(solution);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return new List<HighlightingInfo>();
                }
            }

            if (solution.GetComponent<IShellLocks>().IsWriteLockHeld())
                throw new InvalidOperationException("Daemon analysis must not run under a write lock.");

            CollectingDaemonProcess process;
            try
            {
                process = new CollectingDaemonProcess(sourceFile, settings);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Base ctor can throw (missing read access, file not analyzable). Degrade gracefully.
                return new List<HighlightingInfo>();
            }

            try
            {
                process.RunHeadless();
                Interruption.Current.CheckAndThrow();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A misbehaving stage must not crash the host: return whatever was collected so far.
            }

            // Defensive copy so callers never depend on the live process instance.
            return new List<HighlightingInfo>(process.Collected);
        }

        /// <summary>
        /// Reports whether the given highlighting actually has at least one available quick-fix, by
        /// asking the real <see cref="QuickFixTable"/> to enumerate the fixes for it — exactly the same
        /// query <c>list_quick_fixes</c> uses (<see cref="QuickFixTable.EnumerateAvailableQuickFixes(HighlightingInfo)"/>,
        /// which yields only fixes whose <c>IsAvailable()</c> returned true).
        /// <para>
        /// This is the accurate ground truth, unlike <c>QuickFixTable.CouldHavePopupQuickFix(IHighlighting)</c>,
        /// which is an unreliable false-negative headless (it can report no fix even when fixes exist).
        /// </para>
        /// <para>
        /// Must be called under a read lock with committed documents. Returns <c>false</c> on non-cancellation failures
        /// so one bad highlighting cannot break a whole diagnostics response.
        /// </para>
        /// </summary>
        /// <param name="solution">The current solution (used to resolve the <see cref="QuickFixTable"/> component).</param>
        /// <param name="info">The highlighting to test for available quick-fixes.</param>
        public static bool HasQuickFix(ISolution solution, HighlightingInfo info)
        {
            if (solution == null || info?.Highlighting == null)
                return false;

            try
            {
                var quickFixTable = solution.GetComponent<QuickFixTable>();
                var instances = quickFixTable.EnumerateAvailableQuickFixes(info);
                if (instances == null)
                    return false;

                foreach (var instance in instances)
                {
                    if (instance?.QuickFix != null)
                        return true;
                }

                return false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // EnumerateAvailableQuickFixes can throw for synthetic/unanalyzable highlightings;
                // treat any failure as "no fix" rather than failing the whole response.
                return false;
            }
        }

        /// <summary>
        /// A minimal headless daemon process. It exists solely so we can call the protected
        /// <c>DaemonProcessBase.DoHighlighting</c> orchestration and capture every highlighting committed
        /// by every stage. The committer parameter type <c>DaemonCommitContext</c> is a <b>protected nested
        /// type</b> of <see cref="DaemonProcessBase"/>; it can only be named here, inside the subclass, so the
        /// committer is a private method and the accumulated highlightings are exposed via the public
        /// <see cref="Collected"/> field (passing the committer through any public/constructor signature would
        /// trigger CS0051 "Inconsistent accessibility").
        /// </summary>
        private sealed class CollectingDaemonProcess : DaemonProcessBase
        {
            public readonly List<HighlightingInfo> Collected = new List<HighlightingInfo>();

            public CollectingDaemonProcess(IPsiSourceFile sourceFile, IContextBoundSettingsStore settings)
                : base(sourceFile, null, settings)
            {
            }

            /// <summary>
            /// Drives the real engine orchestration synchronously over this one file.
            /// VISIBLE_DOCUMENT is the normal interactive per-file inspection set (NOT
            /// SOLUTION_ANALYSIS, which would assert <c>ContentModelFork.AssertForked</c>, and NOT OTHER,
            /// which is not the interactive analysis kind). Because <see cref="RunStagesInParallel"/> is
            /// false, DoHighlighting runs every stage barrier synchronously inline on the calling thread and
            /// returns only after the last stage has committed — so <see cref="Collected"/> is fully
            /// populated by the time this returns.
            /// </summary>
            public void RunHeadless()
            {
                DoHighlighting(DaemonProcessKind.VISIBLE_DOCUMENT, Commit);
            }

            // The committer: invoked once per stage with that stage's DaemonCommitContext. This is the ONLY
            // place the protected nested DaemonCommitContext type can be named.
            private void Commit(DaemonCommitContext ctx)
            {
                var toAdd = ctx?.HighlightingsToAdd;
                if (toAdd == null) return;
                foreach (var info in toAdd)
                {
                    if (info?.Highlighting != null)
                        Collected.Add(info);
                }
            }

            // Force fully-synchronous, single-threaded stage execution so DoHighlighting sets sync=true and
            // completes inline on the calling background thread before RunHeadless returns.
            protected override bool RunStagesInParallel => false;

            // Analyze the whole file (not incremental rehighlighting).
            public override bool FullRehighlightingRequired => true;

            public override bool IsRangeInvalidated(DocumentRange range) => true;

            // SWEA / solution-wide-analysis notifications are not wanted headlessly.
            protected override bool ShouldNotifySwea(IPsiSourceFile sourceFile) => false;

            // Secondary sink: only fires from NotifySolutionAnalysis when ShouldNotifySwea==true, so it is
            // effectively dead here. Kept as a required no-op override.
            protected override void AnalysisStageCompleted(
                IPsiSourceFile sourceFile,
                IDaemonStage stage,
                byte layer,
                List<HighlightingInfo> stageHighlightings,
                bool stageFullRehighlight,
                List<DocumentRange> stageRanges,
                DaemonProcessKind processKind,
                IContextBoundSettingsStore settingsStore)
            {
            }

            // Remaining abstract IDE/SWEA integration points: no-op for headless use.
            protected override void FilePartlyReanalyzed(
                IPsiSourceFile sourceFile,
                DaemonProcessBase daemonProcessBase,
                DaemonProcessKind processKind)
            {
            }

            protected override void AnalysisCompleted(
                IPsiSourceFile sourceFile,
                DaemonProcessBase daemonProcessBase,
                DependencySet dependencies,
                bool analysisSupported,
                DaemonProcessKind processKind)
            {
            }
        }
    }
}
