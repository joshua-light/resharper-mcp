using System.Collections.Generic;
using Newtonsoft.Json;

namespace ReSharperMcp.Protocol
{
    public class InitializeResult
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion { get; set; } = "2025-03-26";

        [JsonProperty("capabilities")]
        public ServerCapabilities Capabilities { get; set; } = new ServerCapabilities();

        [JsonProperty("serverInfo")]
        public ServerInfo ServerInfo { get; set; } = new ServerInfo();
    }

    public class ServerCapabilities
    {
        [JsonProperty("tools")]
        public ToolsCapability Tools { get; set; } = new ToolsCapability();
    }

    public class ToolsCapability
    {
        [JsonProperty("listChanged")]
        public bool ListChanged { get; set; } = false;
    }

    public class ServerInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "resharper-mcp";

        [JsonProperty("version")]
        public string Version { get; set; } = "0.6.0";
    }

    public class ToolsListResult
    {
        [JsonProperty("tools")]
        public List<ToolDefinition> Tools { get; set; } = new List<ToolDefinition>();
    }

    public class ToolDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("inputSchema")]
        public object InputSchema { get; set; }
    }

    public class CallToolResult
    {
        [JsonProperty("content")]
        public List<ContentBlock> Content { get; set; } = new List<ContentBlock>();

        [JsonProperty("isError")]
        public bool IsError { get; set; }
    }

    public class ContentBlock
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "text";

        [JsonProperty("text")]
        public string Text { get; set; }
    }
}
