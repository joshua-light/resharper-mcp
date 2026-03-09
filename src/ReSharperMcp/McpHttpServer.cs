using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using JetBrains.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReSharperMcp.Protocol;

namespace ReSharperMcp
{
    public class McpHttpServer
    {
        private readonly HttpListener _listener;
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<string, SolutionRegistration> _solutions = new Dictionary<string, SolutionRegistration>();
        private readonly Dictionary<string, PeerRegistration> _peers = new Dictionary<string, PeerRegistration>();
        private Thread _listenerThread;
        private volatile bool _running;

        public int Port { get; }

        public McpHttpServer(int port, ILogger logger)
        {
            Port = port;
            _logger = logger;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void RegisterSolution(string solutionName, string solutionPath,
            List<ToolDefinition> tools, Dictionary<string, Func<JObject, object>> handlers)
        {
            lock (_lock)
            {
                _solutions[solutionPath] = new SolutionRegistration
                {
                    Name = solutionName,
                    Path = solutionPath,
                    Tools = tools,
                    ToolHandlers = handlers
                };
            }
        }

        public void UnregisterSolution(string solutionPath)
        {
            lock (_lock)
            {
                _solutions.Remove(solutionPath);
            }
        }

        public void Start()
        {
            _running = true;
            _listener.Start();
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "ReSharperMcp-HttpListener"
            };
            _listenerThread.Start();
            _logger.Info($"ReSharper MCP server listening on http://127.0.0.1:{Port}/");
        }

        public void Stop()
        {
            _running = false;
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // Ignore errors during shutdown
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_running)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error accepting HTTP connection");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                context.Response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (context.Request.HttpMethod == "OPTIONS")
                {
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                _logger.Verbose($"MCP request: {body}");

                var request = JsonConvert.DeserializeObject<JsonRpcRequest>(body);
                var response = ProcessRequest(request);
                var responseJson = JsonConvert.SerializeObject(response, Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                _logger.Verbose($"MCP response: {responseJson}");

                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error handling MCP request");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // Ignore
                }
            }
        }

        private JsonRpcResponse ProcessRequest(JsonRpcRequest request)
        {
            switch (request.Method)
            {
                case "initialize":
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = new InitializeResult()
                    };

                case "notifications/initialized":
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = new JObject()
                    };

                case "tools/list":
                    return HandleToolsList(request);

                case "tools/call":
                    return HandleToolCall(request);

                case "internal/register":
                    return HandlePeerRegister(request);

                case "internal/deregister":
                    return HandlePeerDeregister(request);

                default:
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = new JsonRpcError
                        {
                            Code = -32601,
                            Message = $"Method not found: {request.Method}"
                        }
                    };
            }
        }

        private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
        {
            var tools = new List<ToolDefinition>();

            lock (_lock)
            {
                // Collect unique tools across all local solutions (they register the same set)
                var seen = new HashSet<string>();
                foreach (var solution in _solutions.Values)
                {
                    foreach (var tool in solution.Tools)
                    {
                        if (seen.Add(tool.Name))
                            tools.Add(tool);
                    }
                }

                // If no local solutions but we have peers, use peer tool info
                if (tools.Count == 0 && _peers.Count > 0)
                {
                    foreach (var peer in _peers.Values)
                    {
                        foreach (var tool in peer.Tools)
                        {
                            if (seen.Add(tool.Name))
                                tools.Add(tool);
                        }
                    }
                }
            }

            // Add solutionName as an optional parameter to each tool's schema
            var enriched = tools.Select(AddSolutionNameParam).ToList();

            // Prepend the list_solutions meta-tool
            enriched.Insert(0, new ToolDefinition
            {
                Name = "list_solutions",
                Description =
                    "List all currently open solutions in Rider. " +
                    "Use this to discover available solution names when multiple solutions are open.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = new string[0]
                }
            });

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new ToolsListResult { Tools = enriched }
            };
        }

        private static ToolDefinition AddSolutionNameParam(ToolDefinition original)
        {
            var schema = JObject.FromObject(original.InputSchema);
            var props = schema["properties"] as JObject ?? new JObject();
            props["solutionName"] = JObject.FromObject(new
            {
                type = "string",
                description =
                    "Target solution name (e.g. 'MyProject'), a unique path segment (e.g. 'my-repo'), or full path. " +
                    "Optional when only one solution is open. " +
                    "Required when multiple solutions are open — use list_solutions to see available names and uniquePathSegment hints."
            });
            schema["properties"] = props;

            return new ToolDefinition
            {
                Name = original.Name,
                Description = original.Description,
                InputSchema = schema
            };
        }

        private JsonRpcResponse HandleToolCall(JsonRpcRequest request)
        {
            var toolName = request.Params?["name"]?.ToString();
            var arguments = request.Params?["arguments"] as JObject ?? new JObject();

            if (toolName == "list_solutions")
                return HandleListSolutions(request);

            // Extract and remove solutionName before passing to the tool handler
            var solutionName = arguments["solutionName"]?.ToString();
            arguments.Remove("solutionName");

            // Resolve the target solution under lock, then execute outside lock
            Func<JObject, object> localHandler = null;
            int peerPort = 0;

            lock (_lock)
            {
                // Collect all known solutions (local + peers)
                var all = new List<SolutionTarget>();

                foreach (var s in _solutions.Values)
                    all.Add(new SolutionTarget { Name = s.Name, Path = s.Path, IsLocal = true });

                foreach (var p in _peers.Values)
                    all.Add(new SolutionTarget { Name = p.SolutionName, Path = p.SolutionPath, IsLocal = false, PeerPort = p.Port });

                if (all.Count == 0)
                    return ToolError(request, "No solutions are currently open in Rider.");

                SolutionTarget target;

                if (solutionName != null)
                {
                    // 1. Try exact name or path match
                    var matches = all
                        .Where(s => s.Name.Equals(solutionName, StringComparison.OrdinalIgnoreCase)
                                    || s.Path.Equals(solutionName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // 2. If ambiguous or no match, try path-segment matching
                    if (matches.Count != 1)
                    {
                        var segmentMatches = all
                            .Where(s => PathContainsSegment(s.Path, solutionName))
                            .ToList();

                        if (segmentMatches.Count == 1)
                        {
                            // Path-segment uniquely identifies a solution
                            matches = segmentMatches;
                        }
                        else if (matches.Count == 0 && segmentMatches.Count > 0)
                        {
                            // No exact matches — use segment matches (may still be ambiguous)
                            matches = segmentMatches;
                        }
                    }

                    if (matches.Count == 0)
                    {
                        var available = string.Join(", ", all.Select(s => $"'{s.Name}'"));
                        return ToolError(request,
                            $"Solution '{solutionName}' not found. Available solutions: {available}");
                    }

                    if (matches.Count > 1)
                    {
                        var disambiguators = ComputeDisambiguators(
                            all.Select(s => new NameAndPath { Name = s.Name, Path = s.Path }).ToList());
                        var available = string.Join("\n",
                            matches.Select(s =>
                            {
                                var hint = disambiguators.TryGetValue(s.Path, out var h) ? h : null;
                                var hintText = hint != null ? $" — use solutionName: \"{hint}\"" : "";
                                return $"  - {s.Name} ({s.Path}){hintText}";
                            }));
                        return ToolError(request,
                            $"Ambiguous solution name '{solutionName}'. Matches:\n{available}");
                    }

                    target = matches[0];
                }
                else if (all.Count == 1)
                {
                    target = all[0];
                }
                else
                {
                    var available = string.Join("\n",
                        all.Select(s => $"  - {s.Name} ({s.Path})"));
                    return ToolError(request,
                        "Multiple solutions are open. Specify 'solutionName' in the arguments.\n" +
                        $"Available solutions:\n{available}");
                }

                if (target.IsLocal)
                {
                    var localSolution = _solutions[target.Path];
                    if (!localSolution.ToolHandlers.TryGetValue(toolName, out localHandler))
                        return ToolError(request, $"Unknown tool: {toolName}");
                }
                else
                {
                    peerPort = target.PeerPort;
                }
            }

            // Execute outside lock
            if (peerPort > 0)
                return ProxyToPeer(request, peerPort, toolName, arguments);

            try
            {
                var result = localHandler(arguments);
                var text = result is string s ? s : JsonConvert.SerializeObject(result, Formatting.Indented);
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Result = new CallToolResult
                    {
                        Content = { new ContentBlock { Text = text } }
                    }
                };
            }
            catch (Exception ex)
            {
                return ToolError(request, $"Error: {ex.Message}");
            }
        }

        private JsonRpcResponse ProxyToPeer(JsonRpcRequest originalRequest, int peerPort, string toolName, JObject arguments)
        {
            try
            {
                var peerRequest = new JsonRpcRequest
                {
                    Id = originalRequest.Id,
                    Method = "tools/call",
                    Params = new JObject
                    {
                        ["name"] = toolName,
                        ["arguments"] = arguments
                    }
                };

                var json = JsonConvert.SerializeObject(peerRequest);
                var url = $"http://127.0.0.1:{peerPort}/";

                var webRequest = (HttpWebRequest)WebRequest.Create(url);
                webRequest.Method = "POST";
                webRequest.ContentType = "application/json";
                webRequest.Timeout = 130000; // slightly more than tool timeout

                var bytes = Encoding.UTF8.GetBytes(json);
                webRequest.ContentLength = bytes.Length;
                using (var stream = webRequest.GetRequestStream())
                    stream.Write(bytes, 0, bytes.Length);

                using (var response = (HttpWebResponse)webRequest.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var responseJson = reader.ReadToEnd();
                    return JsonConvert.DeserializeObject<JsonRpcResponse>(responseJson);
                }
            }
            catch (WebException ex)
            {
                // Peer is unreachable — remove stale registration
                lock (_lock)
                {
                    var staleKey = _peers.FirstOrDefault(p => p.Value.Port == peerPort).Key;
                    if (staleKey != null)
                    {
                        _logger.Warn($"Removing unreachable peer on port {peerPort}");
                        _peers.Remove(staleKey);
                    }
                }

                return ToolError(originalRequest,
                    $"Solution is no longer available (peer on port {peerPort} is unreachable: {ex.Message})");
            }
            catch (Exception ex)
            {
                return ToolError(originalRequest, $"Error proxying to peer: {ex.Message}");
            }
        }

        private JsonRpcResponse HandleListSolutions(JsonRpcRequest request)
        {
            var solutionObjects = new JArray();

            lock (_lock)
            {
                var allEntries = new List<NameAndPath>();

                foreach (var s in _solutions.Values)
                    allEntries.Add(new NameAndPath { Name = s.Name, Path = s.Path });
                foreach (var p in _peers.Values)
                    allEntries.Add(new NameAndPath { Name = p.SolutionName, Path = p.SolutionPath });

                var disambiguators = ComputeDisambiguators(allEntries);

                foreach (var s in _solutions.Values)
                {
                    var obj = new JObject
                    {
                        ["name"] = s.Name,
                        ["path"] = s.Path,
                        ["toolCount"] = s.Tools.Count
                    };
                    if (disambiguators.TryGetValue(s.Path, out var hint))
                        obj["uniquePathSegment"] = hint;
                    solutionObjects.Add(obj);
                }

                foreach (var p in _peers.Values)
                {
                    var obj = new JObject
                    {
                        ["name"] = p.SolutionName,
                        ["path"] = p.SolutionPath,
                        ["toolCount"] = p.Tools.Count
                    };
                    if (disambiguators.TryGetValue(p.SolutionPath, out var hint))
                        obj["uniquePathSegment"] = hint;
                    solutionObjects.Add(obj);
                }
            }

            var result = new JObject
            {
                ["solutionCount"] = solutionObjects.Count,
                ["solutions"] = solutionObjects
            };

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new CallToolResult
                {
                    Content = { new ContentBlock { Text = result.ToString(Formatting.Indented) } }
                }
            };
        }

        #region Peer registration (internal protocol)

        private JsonRpcResponse HandlePeerRegister(JsonRpcRequest request)
        {
            var port = request.Params?["port"]?.Value<int>() ?? 0;
            var name = request.Params?["solutionName"]?.ToString();
            var path = request.Params?["solutionPath"]?.ToString();
            var toolsToken = request.Params?["tools"] as JArray;

            if (port > 0 && !string.IsNullOrEmpty(path))
            {
                var tools = new List<ToolDefinition>();
                if (toolsToken != null)
                {
                    foreach (var t in toolsToken)
                    {
                        tools.Add(new ToolDefinition
                        {
                            Name = t["name"]?.ToString(),
                            Description = t["description"]?.ToString(),
                            InputSchema = t["inputSchema"]
                        });
                    }
                }

                lock (_lock)
                {
                    _peers[path] = new PeerRegistration
                    {
                        SolutionName = name,
                        SolutionPath = path,
                        Port = port,
                        Tools = tools
                    };
                }

                _logger.Info($"Registered peer solution '{name}' on port {port}");
            }

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new JObject { ["ok"] = true }
            };
        }

        private JsonRpcResponse HandlePeerDeregister(JsonRpcRequest request)
        {
            var path = request.Params?["solutionPath"]?.ToString();

            if (!string.IsNullOrEmpty(path))
            {
                lock (_lock)
                {
                    _peers.Remove(path);
                }

                _logger.Info($"Deregistered peer solution at '{path}'");
            }

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new JObject { ["ok"] = true }
            };
        }

        #endregion

        /// <summary>
        /// Checks if <paramref name="segment"/> appears as a complete path segment in <paramref name="path"/>.
        /// E.g. "tps-project" matches ".../tps-project/..." but NOT ".../tps-project-dyn/...".
        /// Also supports multi-segment queries like "tps-project/Client".
        /// </summary>
        private static bool PathContainsSegment(string path, string segment)
        {
            var normalized = "/" + path.Replace("\\", "/") + "/";
            var search = "/" + segment.Replace("\\", "/") + "/";
            return normalized.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// For solutions with duplicate names, finds the first parent directory segment
        /// that uniquely identifies each solution. Returns a map of path → unique segment.
        /// </summary>
        private static Dictionary<string, string> ComputeDisambiguators(List<NameAndPath> solutions)
        {
            var result = new Dictionary<string, string>();
            var groups = solutions.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var items = group.ToList();
                if (items.Count <= 1) continue;

                foreach (var item in items)
                {
                    var segments = item.Path.Replace("\\", "/").Split('/');
                    // Walk from right to left, skipping the filename
                    for (var i = segments.Length - 2; i >= 0; i--)
                    {
                        var seg = segments[i];
                        if (string.IsNullOrEmpty(seg)) continue;

                        var wrappedSeg = "/" + seg + "/";
                        var matchCount = items.Count(other =>
                            ("/" + other.Path.Replace("\\", "/") + "/")
                                .IndexOf(wrappedSeg, StringComparison.OrdinalIgnoreCase) >= 0);

                        if (matchCount == 1)
                        {
                            result[item.Path] = seg;
                            break;
                        }
                    }
                }
            }

            return result;
        }

        private static JsonRpcResponse ToolError(JsonRpcRequest request, string message)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new CallToolResult
                {
                    IsError = true,
                    Content = { new ContentBlock { Text = message } }
                }
            };
        }
    }

    internal class SolutionRegistration
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public List<ToolDefinition> Tools { get; set; }
        public Dictionary<string, Func<JObject, object>> ToolHandlers { get; set; }
    }

    internal class PeerRegistration
    {
        public string SolutionName { get; set; }
        public string SolutionPath { get; set; }
        public int Port { get; set; }
        public List<ToolDefinition> Tools { get; set; }
    }

    internal class SolutionTarget
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsLocal { get; set; }
        public int PeerPort { get; set; }
    }

    internal class NameAndPath
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }
}
