using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly Dictionary<string, Func<JObject, object>> _toolHandlers = new Dictionary<string, Func<JObject, object>>();
        private readonly List<ToolDefinition> _tools = new List<ToolDefinition>();
        private readonly ILogger _logger;
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

        public void RegisterTool(ToolDefinition definition, Func<JObject, object> handler)
        {
            _tools.Add(definition);
            _toolHandlers[definition.Name] = handler;
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
                    // This is a notification, no response needed but we return one anyway
                    // since the client sent it as a request over HTTP
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = new JObject()
                    };

                case "tools/list":
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = new ToolsListResult { Tools = _tools }
                    };

                case "tools/call":
                    return HandleToolCall(request);

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

        private JsonRpcResponse HandleToolCall(JsonRpcRequest request)
        {
            var toolName = request.Params?["name"]?.ToString();
            var arguments = request.Params?["arguments"] as JObject ?? new JObject();

            if (toolName == null || !_toolHandlers.TryGetValue(toolName, out var handler))
            {
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Result = new CallToolResult
                    {
                        IsError = true,
                        Content = { new ContentBlock { Text = $"Unknown tool: {toolName}" } }
                    }
                };
            }

            try
            {
                var result = handler(arguments);
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
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Result = new CallToolResult
                    {
                        IsError = true,
                        Content = { new ContentBlock { Text = $"Error: {ex.Message}" } }
                    }
                };
            }
        }
    }
}
