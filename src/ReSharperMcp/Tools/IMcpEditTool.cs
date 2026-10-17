using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Marker for tools that edit source files. They are refused while Rider's copy of a file differs from disk,
    /// because the edit would be applied to the old text and saved over the newer one. A call with
    /// <c>dryRun: true</c> edits nothing and is never refused.
    /// </summary>
    /// <remarks>
    /// Orthogonal to <see cref="IMcpWriteTool"/> and <see cref="IMcpAsyncTool"/>, which choose a thread and a lock.
    /// </remarks>
    public interface IMcpEditTool : IMcpTool
    {
    }

    /// <summary>
    /// Marker for tools whose arguments name more files than they list, e.g. "every file changed since a git ref".
    /// <see cref="Expand"/> runs before any lock is taken, so it may touch the file system or start processes.
    /// </summary>
    public interface IMcpExpandableTool : IMcpTool
    {
        JObject Expand(JObject arguments);
    }
}
