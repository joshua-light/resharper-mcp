using System;
using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using Newtonsoft.Json.Linq;
using ReSharperMcp.Protocol;
using ReSharperMcp.Tools;

namespace ReSharperMcp
{
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class McpServerComponent : IDisposable
    {
        private const int DefaultPort = 23741;
        private const int ToolTimeoutSeconds = 120;
        private readonly McpHttpServer _server;
        private readonly ILogger _logger;

        public McpServerComponent(
            Lifetime lifetime,
            ISolution solution,
            IShellLocks shellLocks,
            ILogger logger)
        {
            _logger = logger;

            var port = GetPort();
            _server = new McpHttpServer(port, logger);

            // Register all tools
            RegisterTool(new FindUsagesTool(solution), shellLocks, solution);
            RegisterTool(new GetSymbolInfoTool(solution), shellLocks, solution);
            RegisterTool(new FindImplementationsTool(solution), shellLocks, solution);
            RegisterTool(new GetFileErrorsTool(solution), shellLocks, solution);
            RegisterTool(new SearchSymbolTool(solution), shellLocks, solution);
            RegisterTool(new GoToDefinitionTool(solution), shellLocks, solution);
            RegisterTool(new GetSolutionStructureTool(solution), shellLocks, solution);
            RegisterTool(new BrowseNamespaceTool(solution), shellLocks, solution);
            RegisterTool(new ListSymbolsInFileTool(solution), shellLocks, solution);

            try
            {
                _server.Start();
                _logger.Info($"ReSharper MCP server started on port {port}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to start ReSharper MCP server");
            }

            lifetime.OnTermination(this);
        }

        private void RegisterTool(IMcpTool tool, IShellLocks shellLocks, ISolution solution)
        {
            _server.RegisterTool(
                new ToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema
                },
                args => ExecuteOnPsiThread(tool, args, shellLocks, solution));
        }

        private object ExecuteOnPsiThread(IMcpTool tool, JObject args, IShellLocks shellLocks, ISolution solution)
        {
            object result = null;
            Exception caught = null;
            var done = new ManualResetEventSlim(false);
            var cancelled = new CancellationTokenSource();

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
            _server?.Stop();
            _logger.Info("ReSharper MCP server stopped");
        }

        private static int GetPort()
        {
            var envPort = Environment.GetEnvironmentVariable("RESHARPER_MCP_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var port))
                return port;
            return DefaultPort;
        }
    }
}
