using System;
using System.Threading.Tasks;
using JetBrains.Application;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Files;

namespace ReSharperMcp
{
  /// <summary>
  ///   Runs interruptible PSI reads and optional main-thread continuations against committed documents.
  /// </summary>
  public sealed class McpToolExecution
  {
    private readonly Lifetime _lifetime;
    private readonly IShellLocks _locks;
    private readonly IReadConstraint _committed;

    public McpToolExecution(Lifetime lifetime, IShellLocks locks, ISolution solution)
    {
      _lifetime = lifetime;
      _locks = locks;
      _committed = new AllDocumentsAreCommittedReadConstraint(solution.GetPsiServices().Files);
    }

    /// <remarks>
    ///   The callback may be retried when IDE writes interrupt it; it must have no side effects.
    /// </remarks>
    public Task<T> Read<T>(Func<T> read) =>
      _locks.StartConstrainedReadActionAsync(_lifetime, _committed, () =>
      {
        var result = read();
        Interruption.Current.CheckAndThrow();

        return result;
      });

    /// <remarks>
    ///   Prepare may be retried. The SDK reruns it if another write invalidates its main-thread continuation.
    ///   Only the continuation may modify the model.
    /// </remarks>
    public Task<T> ReadThenMain<T>(Func<ReadAndWriteScope, ReadAndWriteScope.ReadResult<T>> prepare) =>
      _locks.StartConstrainedReadAndMainThreadActionAsync(_lifetime, _committed, scope =>
      {
        var result = prepare(scope);
        Interruption.Current.CheckAndThrow();

        return result;
      });
  }
}
