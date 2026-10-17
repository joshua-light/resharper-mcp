#nullable enable
using System;
using System.Collections.Generic;
using JetBrains.Application;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using JetBrains.Util.Logging;

namespace ReSharperMcp
{
  /// <summary>
  ///   Elements a solution- or file-wide walk stepped over because ReSharper threw while reading them.
  /// </summary>
  /// <remarks>
  ///   Observed: one type part out of sync with the symbol cache throws <c>AssertionException</c> from
  ///   <c>GetMembers()</c>, and an unguarded walk lost every result with it. A guarded walk loses one element.
  /// </remarks>
  public sealed class SkippedElements
  {
    private const int ListedLabels = 5;

    private static readonly ILogger Log = Logger.GetLogger<SkippedElements>();

    private readonly List<string> _labels = new List<string>();

    public int Count => _labels.Count;

    /// <summary>
    ///   One line for the end of a tool result, or <c>null</c> when nothing was skipped.
    /// </summary>
    public string? Line
    {
      get
      {
        if (_labels.Count == 0)
          return null;

        var listed = string.Join(", ", _labels.GetRange(0, Math.Min(_labels.Count, ListedLabels)));
        var more = _labels.Count > ListedLabels ? $", +{_labels.Count - ListedLabels} more" : "";
        var noun = _labels.Count == 1 ? "element" : "elements";

        return $"skipped {_labels.Count} {noun} ReSharper failed to read ({listed}{more})";
      }
    }

    public void Guard(IDeclaredElement element, Action visit) =>
      Guard(() => PsiHelpers.GetQualifiedName(element), visit);

    public void Guard(string label, Action visit) =>
      Guard(() => label, visit);

    public void Guard(Func<string> label, Action visit)
    {
      try
      {
        visit();
      }
      catch (Exception exception) when (exception is not OperationCanceledException)
      {
        Interruption.Current.CheckAndThrow();
        Skip(label, exception);
      }
    }

    private void Skip(Func<string> label, Exception exception)
    {
      var name = SafeLabel(label);
      _labels.Add($"{name}: {exception.GetType().Name}");
      Log.Warn(exception, ExceptionOrigin.Algorithmic, $"ReSharperMcp skipped '{name}'");
    }

    /// <remarks>
    ///   The element that broke the walk may break its own name, too.
    /// </remarks>
    private static string SafeLabel(Func<string> label)
    {
      try
      {
        return label();
      }
      catch (Exception exception) when (exception is not OperationCanceledException)
      {
        return "<unnamed>";
      }
    }
  }
}
