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

    /// <summary>
    /// Marker interface for write tools that manage their own PSI transactions internally.
    /// The component will acquire a write lock but skip the outer PsiTransactionCookie,
    /// allowing the tool (e.g. CodeCleanupRunner) to manage transactions itself.
    /// </summary>
    public interface IMcpSelfTransactingWriteTool : IMcpWriteTool
    {
    }
}
