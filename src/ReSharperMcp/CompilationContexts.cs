#nullable enable
using System;
using System.Linq;
using JetBrains.ReSharper.Psi;

namespace ReSharperMcp
{
  /// <summary>
  ///   The ambient <see cref="CompilationContextCookie"/> ReSharper's analysis expects the IDE to have set.
  /// </summary>
  /// <remarks>
  ///   Without one, anything that asks for runtime features (override and implementation search, language
  ///   injection, rename) falls back to the universal context and throws a NullReferenceException in
  ///   <c>GetRuntimeFeatures</c>, as seen in <c>go_to_definition</c> and <c>find_usages</c>. The context must be the
  ///   element's own: a solution-wide default (tried, then removed) resolves types in the wrong module, printing
  ///   them as <c>???</c> and making projects look unloaded.
  /// </remarks>
  public static class CompilationContexts
  {
    /// <summary>
    ///   The context of the file that declares <paramref name="element"/>; nothing for compiled elements.
    /// </summary>
    public static IDisposable For(IDeclaredElement? element) =>
      For(element?.GetSourceFiles().FirstOrDefault());

    public static IDisposable For(IPsiSourceFile? file) =>
      file?.ResolveContext is { } context ? CompilationContextCookie.GetOrCreate(context) : new Nothing();

    private sealed class Nothing : IDisposable
    {
      public void Dispose()
      {
      }
    }
  }
}
