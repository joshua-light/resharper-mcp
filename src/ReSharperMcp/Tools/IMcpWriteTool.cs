namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Marker interface for tools that modify the PSI tree and require a write lock.
    /// Tools implementing this interface will be executed under WriteLockCookie + PsiTransactionCookie
    /// instead of the default read lock.
    /// </summary>
    public interface IMcpWriteTool : IMcpTool
    {
    }
}
