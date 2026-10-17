using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCleanup;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReSharperMcp.Disk;
using ReSharperMcp.Protocol;
using ReSharperMcp.Tools;

namespace ReSharperMcp
{
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class McpServerComponent : IDisposable
    {
        private const int ToolTimeoutSeconds = 120;
        private readonly McpShellComponent _shellComponent;
        private readonly string _solutionPath;
        private readonly SolutionRoot _root;
        private readonly ILogger _logger;
        private readonly Lifetime _lifetime;
        private readonly Lazy<IPsiCachesState> _caches;
        private readonly DiskChanges _diskChanges;
        private readonly DiskSync _diskSync;

        public McpServerComponent(
            Lifetime lifetime,
            ISolution solution,
            IShellLocks shellLocks,
            McpShellComponent shellComponent,
            CodeCleanupSettingsComponent cleanupSettings,
            ILogger logger)
        {
            _lifetime = lifetime;
            _shellComponent = shellComponent;
            _logger = logger;
            _caches = new Lazy<IPsiCachesState>(() => solution.TryGetComponent<IPsiCachesState>());
            _solutionPath = solution.SolutionFilePath?.FullPath ?? "";
            _root = SolutionRoot.Of(solution);
            _diskChanges = new DiskChanges();
            _diskSync = new DiskSync(solution, shellLocks, _diskChanges);
            lifetime.OnTermination(_diskChanges);

            // Watching a large tree can take a while on Linux (one inotify watch per directory).
            if (_root.Workspace != null)
                ThreadPool.QueueUserWorkItem(_ => _diskChanges.Watch(_root.Workspace));

            var solutionName = string.IsNullOrEmpty(_solutionPath)
                ? "Unknown"
                : Path.GetFileNameWithoutExtension(_solutionPath);

            var tools = new List<ToolDefinition>();
            var handlers = new Dictionary<string, Func<JObject, object>>();

            // Register all tools
            RegisterTool(new FindUsagesTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GetSymbolInfoTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new FindImplementationsTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GetFileErrorsTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new SearchSymbolTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GoToDefinitionTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GetSolutionStructureTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new BrowseNamespaceTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new ListSymbolsInFileTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new FixUsingsTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new FormatFileTool(solution, cleanupSettings), shellLocks, solution, tools, handlers);
            RegisterTool(new FlowTool(solution), shellLocks, solution, tools, handlers);

            // New agentic tools (v0.9.0)
            RegisterTool(new GetSymbolSourceTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GetCallHierarchyTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GetTypeHierarchyTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GetDiagnosticsTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new ListQuickFixesTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new ApplyQuickFixTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new RenameSymbolTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new GenerateMembersTool(solution), shellLocks, solution, tools, handlers);
            RegisterTool(new CompleteAtTool(solution), shellLocks, solution, tools, handlers);

            // File-wide scoped quick-fix application (v0.10.0)
            RegisterTool(new ApplySuggestionsTool(solution), shellLocks, solution, tools, handlers);

            _shellComponent.RegisterSolution(solutionName, _solutionPath, tools, handlers, Health);

            lifetime.OnTermination(this);
        }

        private bool IsIndexing => _caches.Value is { } caches && !caches.IsIdle.Value;

        private JObject Health() => new JObject
        {
            ["indexing"] = IsIndexing,
            ["pendingDiskChanges"] = _diskSync.Pending,
        };

        private void RegisterTool(IMcpTool tool, IShellLocks shellLocks, ISolution solution,
            List<ToolDefinition> tools, Dictionary<string, Func<JObject, object>> handlers)
        {
            tools.Add(new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema
            });
            handlers[tool.Name] = args => ExecuteOnPsiThread(tool, args, shellLocks, solution);
        }

        private object ExecuteOnPsiThread(IMcpTool tool, JObject args, IShellLocks shellLocks, ISolution solution)
        {
            return ExecuteAsync(tool, args, shellLocks, solution).GetAwaiter().GetResult();
        }

        private async Task<object> ExecuteAsync(IMcpTool tool, JObject args, IShellLocks shellLocks, ISolution solution)
        {
            using var request = Lifetime.Define(_lifetime, $"ReSharperMcp.{tool.Name}");
            using var timer = new CancellationTokenSource();
            var execution = new McpToolExecution(request.Lifetime, shellLocks, solution);
            var task = RunAsync();
            var timeout = Task.Delay(TimeSpan.FromSeconds(ToolTimeoutSeconds), timer.Token);

            if (await Task.WhenAny(task, timeout).ConfigureAwait(false) != task)
            {
                request.Terminate();
                _ = task.ContinueWith(completed => _logger.LogException(completed.Exception),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new TimeoutException(
                    $"Timed out after {ToolTimeoutSeconds}s processing '{tool.Name}'. "
                    + "The IDE may be busy indexing or performing another operation.");
            }

            timer.Cancel();

            return await task.ConfigureAwait(false);

            async Task<object> RunAsync()
            {
                // Files the caller named, not the ones an expansion added: only those deserve a note of their own.
                var requested = FileArguments.Absolute(args, _root);
                if (tool is IMcpExpandableTool expandable)
                    args = expandable.Expand(args);

                var disk = await _diskSync.Run(request.Lifetime, execution, FileArguments.Absolute(args, _root))
                    .ConfigureAwait(false);

                var refusal = Refusal(disk);
                if (refusal != null)
                    return new { error = refusal };

                var result = await (tool switch
                {
                    IMcpAsyncTool asyncTool => asyncTool.ExecuteAsync(args, execution),
                    IMcpWriteTool => ExecuteWriteAsync(),
                    _ => execution.Read(() => tool.Execute(args)),
                }).ConfigureAwait(false);

                return WithNotes(result, Notes(disk, requested));
            }

            Task<object> ExecuteWriteAsync() =>
                shellLocks.ExecuteOrQueueReadLockAsync(request.Lifetime, $"ReSharperMcp.{tool.Name}", () =>
                {
                    object result = null;
                    shellLocks.ExecuteWithWriteLock(() =>
                    {
                        if (!request.Lifetime.IsAlive)
                            throw new OperationCanceledException();

                        solution.GetPsiServices().Files.CommitAllDocuments();
                        if (tool is IMcpSelfTransactingWriteTool)
                        {
                            result = tool.Execute(args);
                        }
                        else
                        {
                            using var transaction = PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(
                                solution.GetPsiServices(), $"ReSharperMcp.{tool.Name}");
                            result = tool.Execute(args);
                        }
                    });

                    return result;
                });

            // An edit applied to Rider's old copy of a file would be saved over the newer text on disk.
            string Refusal(DiskReport disk)
            {
                if (tool is not IMcpEditTool || args["dryRun"]?.Value<bool>() == true)
                    return null;

                if (disk.Stale.Count == 0)
                    return null;

                return $"Refused: {tool.Name} edits Rider's copy of files and saves it, and Rider's copy of these "
                    + "differs from disk (unsaved edits in Rider, or Rider has not reloaded the file yet):"
                    + string.Concat(disk.Stale.Select(x => $"\n  {x}"))
                    + "\nSave or revert unsaved edits in Rider, or re-run in a few seconds.";
            }
        }

        private List<string> Notes(DiskReport disk, IReadOnlyList<string> requested)
        {
            var notes = new List<string>();
            if (disk.Stale.Count > 0)
                notes.Add($"note: Rider's copy of {Listed(disk.Stale)} differs from disk (unsaved edits in Rider, or not "
                    + "reloaded yet); results reflect Rider's copy");

            var absent = disk.Absent.Where(requested.Contains).ToList();
            if (absent.Count > 0)
                notes.Add($"note: {Listed(absent)} {(absent.Count == 1 ? "is" : "are")} on disk but in no project yet");

            if (IsIndexing)
                notes.Add("(index still building — results may be incomplete)");

            return notes;
        }

        private string Listed(IReadOnlyList<string> files)
        {
            const int shown = 3;
            var listed = string.Join(", ", files.Take(shown).Select(_root.Relative));

            return files.Count > shown ? $"{files.Count} files ({listed}, +{files.Count - shown} more)" : listed;
        }

        private static object WithNotes(object result, List<string> notes)
        {
            if (notes.Count == 0)
                return result;

            var text = result as string ?? JsonConvert.SerializeObject(result, Formatting.Indented);

            return $"{text}\n\n{string.Join("\n", notes)}";
        }

        public void Dispose()
        {
            _shellComponent.UnregisterSolution(_solutionPath);
        }
    }
}
