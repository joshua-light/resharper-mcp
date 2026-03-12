using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.Util;
using Newtonsoft.Json.Linq;
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
        private readonly ILogger _logger;

        public McpServerComponent(
            Lifetime lifetime,
            ISolution solution,
            IShellLocks shellLocks,
            McpShellComponent shellComponent,
            ILogger logger)
        {
            _shellComponent = shellComponent;
            _logger = logger;
            _solutionPath = solution.SolutionFilePath?.FullPath ?? "";

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

            _shellComponent.RegisterSolution(solutionName, _solutionPath, tools, handlers);

            lifetime.OnTermination(this);
        }

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
            object result = null;
            Exception caught = null;
            var done = new ManualResetEventSlim(false);
            var cancelled = new CancellationTokenSource();

            if (tool is IMcpWriteTool)
            {
                // Write tools need a write lock + PsiTransaction for PSI modifications.
                // Write lock can only be acquired on the Primary Thread, so:
                // 1. ExecuteOrQueue dispatches to the primary thread
                // 2. ExecuteWithWriteLock acquires the exclusive write lock there
                shellLocks.ExecuteOrQueue(
                    $"ReSharperMcp.{tool.Name}",
                    () =>
                    {
                        shellLocks.ExecuteWithWriteLock(() =>
                        {
                            if (cancelled.IsCancellationRequested)
                                return;

                            try
                            {
                                solution.GetPsiServices().Files.CommitAllDocuments();
                                using (PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(
                                    solution.GetPsiServices(), $"ReSharperMcp.{tool.Name}"))
                                {
                                    result = tool.Execute(args);
                                }
                            }
                            catch (Exception ex)
                            {
                                caught = ex;
                            }
                            finally
                            {
                                done.Set();
                            }
                        });
                    });
            }
            else
            {
                shellLocks.ExecuteOrQueueReadLock(
                    $"ReSharperMcp.{tool.Name}",
                    () =>
                    {
                        if (cancelled.IsCancellationRequested)
                            return;

                        try
                        {
                            solution.GetPsiServices().Files.CommitAllDocuments();
                            result = tool.Execute(args);
                        }
                        catch (Exception ex)
                        {
                            caught = ex;
                        }
                        finally
                        {
                            done.Set();
                        }
                    });
            }

            if (!done.Wait(TimeSpan.FromSeconds(ToolTimeoutSeconds)))
            {
                cancelled.Cancel();
                throw new TimeoutException(
                    $"Timed out after {ToolTimeoutSeconds}s waiting for R# to process '{tool.Name}'. " +
                    "The IDE may be busy indexing or performing another operation.");
            }

            if (caught != null)
                throw caught;

            return result;
        }

        public void Dispose()
        {
            _shellComponent.UnregisterSolution(_solutionPath);
        }
    }
}
