using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Refactorings;
using JetBrains.ReSharper.Feature.Services.Refactorings.Conflicts;
using JetBrains.ReSharper.Feature.Services.Refactorings.Specific.Rename;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Refactorings.Rename;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Safe, semantic, solution-wide rename. This is a self-transacting WRITE tool
    /// (<see cref="IMcpSelfTransactingWriteTool"/>): per the runtime contract the component executes
    /// it on the R# main thread with a write lock held and documents already committed, but it does
    /// NOT open a PSI transaction for us. We therefore open our own transaction so we can control
    /// whether it commits (a real rename) or is discarded (a dry-run).
    ///
    /// The actual rename is driven by the standard ReSharper rename machinery:
    ///   RenameWorkflow + RenameRefactoring (the high-level executer) over a RenameDataModel.
    /// The data model is populated via <c>LoadBaseRenames</c> (which builds the atomic renames AND
    /// searches the solution for usages — this is what makes the rename semantic, not textual) and
    /// then <c>RenameRefactoring.Execute</c> performs declaration + usage rebinding.
    ///
    /// Conflicts are collected through a <see cref="RefactoringDriverWithConflicts"/> (the plain
    /// RefactoringDriver discards conflicts), and the driver-collected <see cref="IConflict"/>s are
    /// shaped into the output.
    /// </summary>
    public class RenameSymbolTool : IMcpSelfTransactingWriteTool
    {
        private readonly ISolution _solution;

        public RenameSymbolTool(ISolution solution) => _solution = solution;

        public string Name => "rename_symbol";

        public string Description =>
            "Safely rename a code symbol (class, method, property, field, parameter, local, etc.) " +
            "and all of its references across the entire solution. This is a semantic rename: it " +
            "updates real usages, not text matches. Provide either a symbolName (e.g. 'MyClass' or " +
            "'Namespace.MyClass') or a file path with position (line/column), plus the required " +
            "'newName'. Returns any conflicts the refactoring detected. Set 'dryRun' to true to " +
            "preview conflicts and affected files WITHOUT modifying any code.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to rename (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                newName = new { type = "string", description = "The new name for the symbol (required)." },
                dryRun = new { type = "boolean", description = "When true, detect conflicts and report affected files WITHOUT applying any changes. Default: false." }
            },
            required = new[] { "newName" }
        };

        public object Execute(JObject arguments)
        {
            var newName = arguments["newName"]?.ToString();
            if (string.IsNullOrWhiteSpace(newName))
                return new { error = "'newName' is required and must be a non-empty string." };

            var dryRun = arguments["dryRun"]?.Value<bool>() ?? false;

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            var oldName = declaredElement.ShortName;

            // Same name: nothing to do.
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return new
                {
                    applied = false,
                    oldName,
                    newName,
                    conflicts = new object[0],
                    note = "New name is identical to the current name; nothing to rename."
                };
            }

            // Verify the element can actually be renamed before we set anything up.
            var factory = new AtomicRenamesFactory();
            var availability = factory.CheckRenameAvailability(declaredElement);
            if (availability != RenameAvailabilityCheckResult.CanBeRenamed)
            {
                return new
                {
                    error = $"Symbol '{oldName}' cannot be renamed: {availability}.",
                    oldName,
                    newName
                };
            }

            // Record declaration locations up front so we can still report them after the
            // PSI tree has been mutated (declarations become invalid post-rename).
            var declarationFiles = declaredElement.GetDeclarations()
                .Select(d => d.GetSourceFile()?.GetLocation().FullPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            var psiServices = _solution.GetPsiServices();

            // The plain RefactoringDriver swallows conflicts; the WithConflicts variant records them.
            var storage = new RefactoringDriverStorage();
            var driver = new RefactoringDriverWithConflicts(storage);

            // A short-lived lifetime for the workflow/data-model scope.
            var lifetimeDefinition = new LifetimeDefinition();
            try
            {
                var lifetime = lifetimeDefinition.Lifetime;

                // Subclass exposes DataModel assignment (the base setter is protected) and lets us
                // drive the workflow without a UI IDataContext.
                var workflow = new HeadlessRenameWorkflow(_solution, "ReSharperMcp.Rename")
                {
                    WorkflowExecuterLifetime = lifetime
                };

                var languageType = RenameUtil.GetPsiLanguageTypeOrKnownLanguage(declaredElement);
                var renameHelper = workflow.LanguageSpecific[languageType];
                if (renameHelper == null)
                    return new { error = $"Rename is not supported for this symbol's language.", oldName, newName };

                // Build the options model. NOTE: the RenameDataModel constructor mutates this
                // model (it sets HasUI=true, RenameDerived=true, ChangeTextOccurrences from
                // settings), so we must force our non-interactive, code-only flags AFTER the
                // data model has been constructed — not before.
                var model = renameHelper.GetOptionsModel(declaredElement, null, lifetime);

                var dataModel = new RenameDataModel(
                    new[] { declaredElement },
                    null,
                    RenameFilesOption.NothingToRename,
                    lifetime,
                    _solution,
                    model,
                    workflow)
                {
                    NewName = newName
                };

                // Force non-interactive, code-only behaviour (overriding the ctor's defaults).
                // IMPORTANT: QuickRename must stay false. QuickRename=true short-circuits the
                // solution-wide usage search in LoadBaseRenames (it is meant for inline/local
                // rename where usages are trivial), which would leave references un-rewritten.
                // With QuickRename=false the workflow searches the whole solution for usages.
                // HasUI=false keeps it headless (no pages/dialogs are shown).
                model.HasUI = false;
                model.QuickRename = false;
                model.RenameDerived = false;
                model.RenameFile = false;
                model.ChangeTextOccurrences = false;

                workflow.AssignDataModel(dataModel);

                // We are a self-transacting write tool: the component holds the write lock but did
                // NOT open a PSI transaction for us. Open our own. For a real rename we use the
                // auto-committing cookie (commits on dispose). For a dry-run we use a temporary
                // change cookie, which rolls back on dispose so none of the edits Execute() makes
                // are ever persisted — while the conflict/affected-file analysis we capture from the
                // (now discarded) tree mutation remains valid for reporting.
                using (var transaction = dryRun
                    ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "ReSharperMcp.rename_symbol")
                    : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "ReSharperMcp.rename_symbol"))
                {
                    // Build atomic renames AND search the solution for usages (populates the
                    // references store the atomic renames consume). NameHasChanged ensures the load
                    // is not short-circuited.
                    dataModel.NameHasChanged = true;
                    dataModel.LoadBaseRenames(NullProgressIndicator.Create(), workflow, forceReload: true);

                    if (dataModel.AllRenames.Renames.Count == 0)
                    {
                        transaction.Rollback();
                        return new { error = $"No applicable renames were produced for '{oldName}'.", oldName, newName };
                    }

                    var refactoring = new RenameRefactoring(workflow, _solution, driver);

                    // Execute the rename. RenameRefactoring.Execute performs cold conflict search
                    // (recorded in the driver) and then the actual declaration/usage rewrites.
                    bool executed = refactoring.Execute(NullProgressIndicator.Create());

                    var conflicts = ExtractConflicts(driver);
                    var changedFiles = CollectChangedFiles(dataModel, declarationFiles);

                    if (dryRun)
                    {
                        // The temporary change cookie rolls back on Dispose, discarding all edits.
                        return new
                        {
                            applied = false,
                            dryRun = true,
                            oldName,
                            newName,
                            conflicts,
                            changedFiles
                        };
                    }

                    // The auto-commit cookie commits on Dispose, persisting the rename.
                    return new
                    {
                        applied = executed,
                        oldName,
                        newName,
                        conflicts,
                        changedFiles
                    };
                }
            }
            finally
            {
                lifetimeDefinition.Terminate();
            }
        }

        private static List<object> ExtractConflicts(RefactoringDriverWithConflicts driver)
        {
            var result = new List<object>();
            foreach (var conflict in driver.Conflicts)
            {
                if (conflict == null) continue;
                result.Add(new
                {
                    message = SafeDescription(conflict),
                    severity = conflict.Severity.ToString(),
                    isValid = SafeIsValid(conflict)
                });
            }
            return result;
        }

        private static string SafeDescription(IConflict conflict)
        {
            try { return conflict.Description; }
            catch { return "(conflict description unavailable)"; }
        }

        private static bool SafeIsValid(IConflict conflict)
        {
            try { return conflict.IsValid; }
            catch { return false; }
        }

        /// <summary>
        /// Reports the files affected by the rename. We combine the original declaration files
        /// with the source files of all references that were searched for the renamed elements.
        /// </summary>
        private static List<object> CollectChangedFiles(RenameDataModel dataModel, List<string> declarationFiles)
        {
            var files = new HashSet<string>(declarationFiles ?? new List<string>());

            try
            {
                foreach (var atomic in dataModel.AllRenames.Renames)
                {
                    var primary = atomic.PrimaryDeclaredElement;
                    if (primary == null) continue;

                    try
                    {
                        foreach (var refsInFile in dataModel.GetGroupedElementReferences(primary))
                        {
                            var path = refsInFile.SourceFile?.GetLocation().FullPath;
                            if (!string.IsNullOrEmpty(path))
                                files.Add(path);
                        }
                    }
                    catch
                    {
                        // Best-effort: a single atomic's reference enumeration failing should not
                        // sink the whole report.
                    }
                }
            }
            catch
            {
                // Ignore — declaration files alone are still a useful answer.
            }

            return files
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (object)new { file = f, fileName = Path.GetFileName(f) })
                .ToList();
        }

        /// <summary>
        /// Headless rename workflow: the base <c>DataModel</c> setter is <c>protected</c> and the
        /// usual <c>Initialize(IDataContext)</c> path requires UI data we do not have, so we
        /// subclass to inject a pre-built data model directly.
        /// </summary>
        private sealed class HeadlessRenameWorkflow : RenameWorkflow
        {
            public HeadlessRenameWorkflow(ISolution solution, string actionId)
                : base(solution, actionId)
            {
            }

            public void AssignDataModel(RenameDataModel dataModel) => DataModel = dataModel;
        }
    }
}
