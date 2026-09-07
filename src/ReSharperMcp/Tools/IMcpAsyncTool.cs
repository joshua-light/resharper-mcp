using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
  /// <summary>
  ///   Coordinates separate read and mutation phases without holding a lock between them.
  /// </summary>
  public interface IMcpAsyncTool : IMcpTool
  {
    Task<object> ExecuteAsync(JObject arguments, McpToolExecution execution);
  }
}
