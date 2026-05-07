namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Marker for tools that manage their own threading and must not be wrapped in a PSI read/write lock.
    /// </summary>
    public interface IMcpDirectTool : IMcpTool
    {
    }
}