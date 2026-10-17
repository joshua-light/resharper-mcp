#nullable enable
using JetBrains.Application.Progress;
using JetBrains.Application.Settings;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.ExternalSources.MetadataViewer;
using JetBrains.ReSharper.Feature.Services.ExternalSources.MetadataTranslator;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Modules;

namespace ReSharperMcp
{
  /// <summary>
  ///   C# source of a type compiled into a referenced assembly, produced by ReSharper's own decompiler.
  /// </summary>
  /// <remarks>
  ///   Method bodies come only when the user has accepted ReSharper's decompiler legal notice in Rider; until then
  ///   the source is a metadata view (signatures only). The notice is Rider's to show, not a headless tool's.
  /// </remarks>
  public sealed class DecompiledSource
  {
    private DecompiledSource(ITypeElement type, string text, bool hasBodies)
    {
      Type = type;
      Text = text;
      HasBodies = hasBodies;
    }

    /// <summary>
    ///   The top-level type that was decompiled; for a member or nested type, the type that contains it.
    /// </summary>
    public ITypeElement Type { get; }

    public string Text { get; }

    public bool HasBodies { get; }

    public string Label => HasBodies
      ? $"// decompiled by ReSharper from {PsiHelpers.AssemblyLabel(Type)}"
      : $"// metadata view of {PsiHelpers.AssemblyLabel(Type)}: signatures only. For method bodies, accept "
        + "ReSharper's decompiler notice in Rider once (navigate into any compiled type).";

    /// <returns><c>null</c> for source elements or when no translator is available.</returns>
    public static DecompiledSource? Of(ISolution solution, IDeclaredElement element)
    {
      var type = element as ITypeElement ?? (element as IClrDeclaredElement)?.GetContainingType();
      while (type?.GetContainingType() != null)
        type = type.GetContainingType();

      if (type?.Module is not IAssemblyPsiModule module)
        return null;

      var translator = LanguageManager.Instance.TryGetService<IMetadataTranslator>(CSharpLanguage.Instance);
      if (translator == null || !translator.IsAvailable)
        return null;

      var hasBodies = translator.IsMethodBodiesDecompilationSupported && IsNoticeAccepted(solution);
      var options = solution.GetComponent<TranslatorOptions>().GetOptions() with { DecompileMethodBodies = hasBodies };
      var text = translator.TranslateTopLevelTypeElement(type, module, options, null, out _, NullProgressIndicator.Create());

      return text == null ? null : new DecompiledSource(type, text, hasBodies);
    }

    private static bool IsNoticeAccepted(ISolution solution) =>
      solution.GetComponent<ISettingsStore>()
        .BindToContextTransient(ContextRange.ApplicationWide)
        .GetValue((DecompilerSettings x) => x.DecompilerLegalNoticeAccepted);
  }
}
