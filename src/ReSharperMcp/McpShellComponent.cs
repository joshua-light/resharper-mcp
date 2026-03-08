using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.Lifetimes;
using JetBrains.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReSharperMcp.Protocol;

namespace ReSharperMcp
{
    [ShellComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class McpShellComponent : IDisposable
    {
        private const int DefaultPort = 23741;
        private const int MaxPortAttempts = 10;
        private readonly McpHttpServer _server;
        private readonly ILogger _logger;
        private readonly int _primaryPort;
        private readonly bool _isPrimary;

        public int Port => _server?.Port ?? 0;

        public McpShellComponent(Lifetime lifetime, ILogger logger)
        {
            _logger = logger;
            var basePort = GetPort();
            _primaryPort = basePort;

            // Try to bind the primary port; if taken, try subsequent ports
            for (var attempt = 0; attempt < MaxPortAttempts; attempt++)
            {
                var tryPort = basePort + attempt;
                var tryServer = new McpHttpServer(tryPort, logger);
                try
                {
                    tryServer.Start();
                    _server = tryServer;
                    _isPrimary = (tryPort == basePort);

                    if (_isPrimary)
                        _logger.Info($"ReSharper MCP primary server started on port {tryPort}");
                    else
                        _logger.Info($"ReSharper MCP peer server started on port {tryPort} (primary on {basePort})");

                    break;
                }
                catch (Exception ex)
                {
                    tryServer.Stop();
                    if (attempt == MaxPortAttempts - 1)
                        _logger.Error(ex, $"Failed to bind any port in range {basePort}-{tryPort} for MCP server");
                    else
                        _logger.Info($"Port {tryPort} unavailable, trying next...");
                }
            }

            lifetime.OnTermination(this);
        }

        public void RegisterSolution(string solutionName, string solutionPath,
            List<ToolDefinition> tools, Dictionary<string, Func<JObject, object>> handlers)
        {
            if (_server == null) return;

            // Always register locally
            _server.RegisterSolution(solutionName, solutionPath, tools, handlers);
            _logger.Info($"Registered solution '{solutionName}' locally on port {_server.Port}");

            // If we're a peer, also notify the primary server
            if (!_isPrimary)
                NotifyPrimary("internal/register", solutionName, solutionPath, tools);
        }

        public void UnregisterSolution(string solutionPath)
        {
            if (_server == null) return;

            _server.UnregisterSolution(solutionPath);

            // If we're a peer, also notify the primary server
            if (!_isPrimary)
                NotifyPrimaryDeregister(solutionPath);
        }

        public void Dispose()
        {
            _server?.Stop();
            _logger.Info("ReSharper MCP server stopped");
        }

        private void NotifyPrimary(string method, string solutionName, string solutionPath, List<ToolDefinition> tools)
        {
            try
            {
                var toolsArray = new JArray();
                foreach (var tool in tools)
                {
                    toolsArray.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = JToken.FromObject(tool.InputSchema)
                    });
                }

                var request = new JsonRpcRequest
                {
                    Id = 1,
                    Method = method,
                    Params = new JObject
                    {
                        ["port"] = _server.Port,
                        ["solutionName"] = solutionName,
                        ["solutionPath"] = solutionPath,
                        ["tools"] = toolsArray
                    }
                };

                SendToPrimary(request);
                _logger.Info($"Registered solution '{solutionName}' with primary server on port {_primaryPort}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to register with primary MCP server: {ex.Message}");
            }
        }

        private void NotifyPrimaryDeregister(string solutionPath)
        {
            try
            {
                var request = new JsonRpcRequest
                {
                    Id = 1,
                    Method = "internal/deregister",
                    Params = new JObject
                    {
                        ["solutionPath"] = solutionPath
                    }
                };

                SendToPrimary(request);
                _logger.Info($"Deregistered solution from primary MCP server");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to deregister from primary MCP server: {ex.Message}");
            }
        }

        private void SendToPrimary(JsonRpcRequest request)
        {
            var json = JsonConvert.SerializeObject(request);
            var url = $"http://127.0.0.1:{_primaryPort}/";

            var webRequest = (HttpWebRequest)WebRequest.Create(url);
            webRequest.Method = "POST";
            webRequest.ContentType = "application/json";
            webRequest.Timeout = 5000;

            var bytes = Encoding.UTF8.GetBytes(json);
            webRequest.ContentLength = bytes.Length;
            using (var stream = webRequest.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);

            using (var response = (HttpWebResponse)webRequest.GetResponse())
            {
                // Just consume the response
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    reader.ReadToEnd();
            }
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
