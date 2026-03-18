using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
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
        private McpHttpServer _server;
        private readonly ILogger _logger;
        private readonly int _primaryPort;
        private bool _isPrimary;

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
                    _server.IsPrimary = _isPrimary;

                    if (_isPrimary)
                        _logger.Info($"ReSharper MCP primary server started on port {tryPort}");
                    else
                        _logger.Info($"ReSharper MCP peer server started on port {tryPort} (primary on {basePort})");

                    // Watchdog: periodically verify the server is responsive
                    var watchdog = new Timer(_ => WatchdogPing(), null,
                        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
                    lifetime.OnTermination(() => watchdog.Dispose());

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

        private void WatchdogPing()
        {
            if (_server == null) return;

            // For peers: check if the primary is still alive; if not, try to take over
            if (!_isPrimary)
            {
                TryPromoteToPrimary();
                return;
            }

            // For primary: verify own listener is responsive
            try
            {
                PingServer(_server.Port);
            }
            catch (Exception ex)
            {
                _logger.Warn($"MCP server health check failed: {ex.Message} — triggering restart");
                _server.Restart();
            }
        }

        /// <summary>
        /// For peer instances: checks if the primary port is available and attempts
        /// to take it over, becoming the new primary.
        /// </summary>
        private void TryPromoteToPrimary()
        {
            try
            {
                // Check if the primary is still responding
                PingServer(_primaryPort);
                // Primary is alive — stay as peer
            }
            catch
            {
                // Primary is unreachable — try to take over
                _logger.Info($"Primary on port {_primaryPort} is unreachable — attempting to take over");

                try
                {
                    var newServer = new McpHttpServer(_primaryPort, _logger);
                    newServer.Start();
                    newServer.IsPrimary = true;

                    // Transfer all solution registrations from old server to new server
                    var oldServer = _server;
                    oldServer.TransferRegistrationsTo(newServer);

                    _server = newServer;
                    _isPrimary = true;

                    oldServer.Stop();

                    _logger.Info($"Promoted to primary on port {_primaryPort} (was peer on {oldServer.Port})");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to take over primary port {_primaryPort}: {ex.Message}");
                }
            }
        }

        private void PingServer(int port)
        {
            var url = $"http://127.0.0.1:{port}/";
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 5000;

            var body = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"internal/status\",\"params\":{}}");
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream())
                stream.Write(body, 0, body.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                reader.ReadToEnd();
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
