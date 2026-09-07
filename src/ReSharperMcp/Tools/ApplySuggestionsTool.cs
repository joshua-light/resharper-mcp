using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Application;
using JetBrains.Application.Threading;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Intentions.Scoped.Actions;
using JetBrains.ReSharper.Feature.Services.Intentions.Scoped.Scopes;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Applies ReSharper inspection quick-fixes across a whole file headlessly, by inspection id —
    /// e.g. converting every explicit constructor into a primary constructor. Drives ReSharper's own
    /// "Fix all in file" engine: for each chosen inspection it runs the scoped quick-fix, which rewrites
    /// every occurrence and re-analyzes until stable.
    ///
    /// Complements <see cref="ApplyQuickFixTool"/> (apply_quick_fix), which applies a single bulb action at a
    /// position. This tool is position-free and file-wide. Only scoped fixes (<see cref="IModernManualScopedAction"/>)
    /// can run this way; non-scoped fixes are reported as skipped — use apply_quick_fix for those.
    ///
    /// Self-transacting: the scoped executor manages its own PSI transactions.
    /// Shares <see cref="DaemonHighlightingCollector"/> with get_diagnostics / list_quick_fixes.
    /// </summary>
    public class ApplySuggestionsTool : IMcpAsyncTool
    {
        private sealed class Pass
        {
            public string TypeName;
            public string Applied;
            public string Error;
            public readonly HashSet<string> Skipped = new();
        }

        // Upper bound on distinct fix types applied per file — a safety net against a fix that never clears its highlighting.
        private const int MaxFixTypesPerFile = 50;

        private readonly ISolution _solution;

        public ApplySuggestionsTool(ISolution solution) => _solution = solution;

        public string Name => "apply_suggestions";

        public string Description =>
            "Apply ReSharper suggestion quick-fixes across a whole file by inspection id (e.g. convert every " +
            "explicit constructor to a primary constructor). Position-free and file-wide — complements " +
            "apply_quick_fix, which applies one fix at a specific position. Run get_diagnostics first to discover " +
            "inspection ids. Specify which to apply via 'inspectionIds', or pass all=true to apply every applicable " +
            "suggestion. Each chosen fix rewrites all its occurrences and re-analyzes until stable. Only headlessly-" +
            "applicable (scoped) fixes are applied; others are reported as skipped. Pass dryRun=true to preview " +
            "without modifying the file. Pass multiple files via the 'filePaths' array.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Absolute path to the file to apply suggestions to"
                },
                filePaths = new
                {
                    type = "array",
                    description = "Array of absolute file paths to process in batch. Results are concatenated with separators. Alternative to single 'filePath' parameter.",
                    items = new { type = "string" }
                },
                inspectionIds = new
                {
                    type = "string",
                    description = "Comma-separated inspection ids to apply (e.g. 'ConvertToPrimaryConstructor'). Use get_diagnostics to discover ids."
                },
                all = new
                {
                    type = "boolean",
                    description = "Apply every applicable (scoped) suggestion in the file. Ignored when 'inspectionIds' is set. Default: false."
                },
                dryRun = new
                {
                    type = "boolean",
                    description = "Report what would be applied without modifying the file. Default: false."
                }
            },
            required = new string[0]
        };

        object IMcpTool.Execute(JObject arguments) =>
            throw new InvalidOperationException("Use the asynchronous tool dispatcher.");

        public async Task<object> ExecuteAsync(JObject arguments, McpToolExecution execution)
        {
            var filePathsToken = arguments["filePaths"] as JArray;
            if (filePathsToken != null && filePathsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < filePathsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = new JObject { ["filePath"] = filePathsToken[i]?.ToString() };
                    CopyIfPresent(arguments, itemArgs, "inspectionIds");
                    CopyIfPresent(arguments, itemArgs, "all");
                    CopyIfPresent(arguments, itemArgs, "dryRun");

                    sb.Append("=== [").Append(i + 1).Append('/').Append(filePathsToken.Count)
                      .Append("] ").Append(filePathsToken[i]).Append(" ===").AppendLine();
                    sb.Append(ResultToString(await ExecuteSingleAsync(itemArgs, execution).ConfigureAwait(false)));
                }
                return sb.ToString().TrimEnd();
            }

            return await ExecuteSingleAsync(arguments, execution).ConfigureAwait(false);
        }

        private async Task<object> ExecuteSingleAsync(JObject arguments, McpToolExecution execution)
        {
            var filePath = arguments["filePath"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            var idFilter = ParseCsv(arguments["inspectionIds"]?.ToString());
            var applyAll = arguments["all"]?.Value<bool>() ?? false;
            var dryRun = arguments["dryRun"]?.Value<bool>() ?? false;

            if (idFilter == null && !applyAll)
                return await execution.Read(() => ListApplicable(filePath)).ConfigureAwait(false);

            if (dryRun)
                return await execution.Read<object>(() =>
                {
                    var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
                    if (sourceFile == null)
                        return new { error = $"File not found in solution: {filePath}" };

                    return DescribeDryRun(filePath, sourceFile,
                        _solution.GetComponent<HighlightingSettingsManager>(), _solution.GetComponent<QuickFixTable>(),
                        idFilter, applyAll);
                }).ConfigureAwait(false);

            var applied = new List<string>();
            var skipped = new HashSet<string>();
            var errors = new List<string>();
            var handledTypes = new HashSet<string>();

            for (var iteration = 0; iteration < MaxFixTypesPerFile; iteration++)
            {
                var pass = await execution.ReadThenMain<Pass>(Prepare).ConfigureAwait(false);
                skipped.UnionWith(pass.Skipped);
                if (pass.Error != null)
                    errors.Add(pass.Error);
                if (pass.TypeName == null)
                    break;

                handledTypes.Add(pass.TypeName);
                if (pass.Applied != null)
                    applied.Add(pass.Applied);
            }

            return FormatResult(filePath, applied, skipped, errors);

            ReadAndWriteScope.ReadResult<Pass> Prepare(ReadAndWriteScope scope)
            {
                var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
                if (sourceFile == null)
                    return scope.Value(new Pass { Error = $"File not found in solution: {filePath}" });

                var settingsManager = _solution.GetComponent<HighlightingSettingsManager>();
                var quickFixTable = _solution.GetComponent<QuickFixTable>();
                var pass = new Pass();
                foreach (var info in DaemonHighlightingCollector.Collect(_solution, sourceFile))
                {
                    Interruption.Current.CheckAndThrow();
                    var inspectionId = GetInspectionId(settingsManager, info.Highlighting);
                    if (!Matches(inspectionId, idFilter, applyAll))
                        continue;

                    foreach (var instance in EnumerateFixes(quickFixTable, info))
                    {
                        if (instance.QuickFix is not IModernManualScopedAction scoped)
                        {
                            if (inspectionId != null)
                                pass.Skipped.Add(inspectionId);
                            continue;
                        }

                        var typeName = instance.QuickFix.GetType().FullName;
                        if (handledTypes.Contains(typeName))
                            continue;

                        var text = FixText(instance);
                        return scope.MainReadAction(() =>
                        {
                            // Scoped actions manage their own writes and may run analysis between transactions.
                            pass.TypeName = typeName;
                            try
                            {
                                scoped.ExecuteAction(_solution, new SourceFileScope(sourceFile), info.Highlighting,
                                    NullProgressIndicator.Create());
                                pass.Applied = $"{inspectionId ?? "(no id)"} — \"{text}\"";
                                _solution.GetPsiServices().Caches.Update();
                            }
                            catch (Exception exception)
                            {
                                pass.Error = $"{inspectionId ?? "(no id)"}: {exception.Message}";
                            }

                            return pass;
                        });
                    }
                }

                return scope.Value(pass);
            }
        }

        // No filter given — list the applicable inspection ids so the caller can choose.
        private object ListApplicable(string filePath)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            var settingsManager = _solution.GetComponent<HighlightingSettingsManager>();
            var quickFixTable = _solution.GetComponent<QuickFixTable>();
            var ids = ApplicableIds(sourceFile, settingsManager, quickFixTable);

            var sb = new StringBuilder();
            sb.Append(filePath).AppendLine(" — specify 'inspectionIds' or pass all=true.");
            if (ids.Count > 0)
            {
                sb.AppendLine().AppendLine("applicable inspection ids in this file:");
                foreach (var id in ids.OrderBy(x => x))
                    sb.Append("  ").AppendLine(id);
            }
            else
            {
                sb.AppendLine().AppendLine("no applicable (scoped) suggestions found.");
            }

            return sb.ToString().TrimEnd();
        }

        private object DescribeDryRun(
            string filePath, IPsiSourceFile sourceFile, HighlightingSettingsManager settingsManager,
            QuickFixTable quickFixTable, HashSet<string> idFilter, bool applyAll)
        {
            var wouldApply = new List<string>();
            var seenTypes = new HashSet<string>();
            foreach (var info in DaemonHighlightingCollector.Collect(_solution, sourceFile))
            {
                var inspectionId = GetInspectionId(settingsManager, info.Highlighting);
                if (!Matches(inspectionId, idFilter, applyAll))
                    continue;

                foreach (var instance in EnumerateFixes(quickFixTable, info))
                {
                    if (!(instance.QuickFix is IModernManualScopedAction))
                        continue;
                    if (!seenTypes.Add(instance.QuickFix.GetType().FullName))
                        continue;
                    wouldApply.Add($"{inspectionId ?? "(no id)"} — \"{FixText(instance)}\"");
                }
            }

            if (wouldApply.Count == 0)
                return $"{filePath} — dry run: nothing to apply";

            var sb = new StringBuilder();
            sb.Append(filePath).Append(" — dry run: would apply ").Append(wouldApply.Count).AppendLine(" fix type(s):");
            foreach (var entry in wouldApply)
                sb.Append("  ").AppendLine(entry);
            return sb.ToString().TrimEnd();
        }

        private HashSet<string> ApplicableIds(
            IPsiSourceFile sourceFile, HighlightingSettingsManager settingsManager, QuickFixTable quickFixTable)
        {
            var ids = new HashSet<string>();
            foreach (var info in DaemonHighlightingCollector.Collect(_solution, sourceFile))
            {
                var inspectionId = GetInspectionId(settingsManager, info.Highlighting);
                if (inspectionId == null)
                    continue;
                foreach (var instance in EnumerateFixes(quickFixTable, info))
                {
                    if (instance.QuickFix is IModernManualScopedAction)
                    {
                        ids.Add(inspectionId);
                        break;
                    }
                }
            }

            return ids;
        }

        private static IEnumerable<QuickFixInstance> EnumerateFixes(QuickFixTable quickFixTable, HighlightingInfo info)
        {
            if (info?.Highlighting == null)
                return Enumerable.Empty<QuickFixInstance>();
            try
            {
                var instances = quickFixTable.EnumerateAvailableQuickFixes(info);
                return instances == null ? Enumerable.Empty<QuickFixInstance>() : instances.Where(i => i?.QuickFix != null).ToList();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A fix's availability check can throw on unusual highlightings — treat as no fixes.
                return Enumerable.Empty<QuickFixInstance>();
            }
        }

        // Configurable inspection id (e.g. "ConvertToPrimaryConstructor"), or null if the highlighting has none.
        private static string GetInspectionId(HighlightingSettingsManager settingsManager, IHighlighting highlighting)
        {
            if (highlighting == null)
                return null;
            if (highlighting is ICustomConfigurableSeverityIdHighlighting custom &&
                !string.IsNullOrEmpty(custom.ConfigurableSeverityId))
                return custom.ConfigurableSeverityId;

            var attribute = settingsManager.GetHighlightingAttribute(highlighting);
            return (attribute as ConfigurableSeverityHighlightingAttribute)?.ConfigurableSeverityId;
        }

        private static bool Matches(string inspectionId, HashSet<string> idFilter, bool applyAll)
        {
            if (applyAll)
                return true;
            return inspectionId != null && idFilter.Contains(inspectionId);
        }

        private static string FixText(QuickFixInstance instance)
        {
            if (instance.QuickFix is IBulbAction bulb && !string.IsNullOrEmpty(bulb.Text))
                return bulb.Text;
            return instance.QuickFix.GetType().Name;
        }

        private static object FormatResult(string filePath, List<string> applied, HashSet<string> skipped, List<string> errors)
        {
            var sb = new StringBuilder();
            sb.Append(filePath).Append(" — applied ").Append(applied.Count).AppendLine(" fix type(s)");

            if (applied.Count > 0)
            {
                sb.AppendLine();
                foreach (var entry in applied)
                    sb.Append("  ✓ ").AppendLine(entry);
            }

            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("skipped (not headlessly applicable — try apply_quick_fix at a position):");
                foreach (var id in skipped.OrderBy(x => x))
                    sb.Append("  ").AppendLine(id);
            }

            if (errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("errors:");
                foreach (var error in errors)
                    sb.Append("  ").AppendLine(error);
            }

            return sb.ToString().TrimEnd();
        }

        private static HashSet<string> ParseCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return null;
            return new HashSet<string>(
                csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);
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
