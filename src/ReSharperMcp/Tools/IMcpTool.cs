using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public interface IMcpTool
    {
        string Name { get; }
        string Description { get; }
        object InputSchema { get; }
        object Execute(JObject arguments);
    }
}
