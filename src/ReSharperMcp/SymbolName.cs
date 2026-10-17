#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSharperMcp
{
  /// <summary>
  ///   A symbol as an agent writes it: <c>Type</c>, <c>Type.Member</c>, <c>Ns.Type.Member</c>, optionally with
  ///   generic arguments (<c>Wait&lt;M&gt;</c>, <c>List`1</c>), a parameter list (<c>Execute(int)</c>) or
  ///   <c>global::</c>.
  /// </summary>
  public sealed class SymbolName
  {
    private static readonly Regex Decorations = new Regex(@"<[^<>]*>|`\d+|\([^()]*\)$", RegexOptions.Compiled);

    private SymbolName(string[] parts, StringComparison comparison)
    {
      Parts = parts;
      Comparison = comparison;
    }

    public string[] Parts { get; }

    public StringComparison Comparison { get; }

    public string Short => Parts[Parts.Length - 1];

    /// <summary>
    ///   The type a member name is qualified with: <c>Type</c> in <c>Ns.Type.Member</c>.
    /// </summary>
    public string? Container => Parts.Length > 1 ? Parts[Parts.Length - 2] : null;

    public bool IsQualified => Parts.Length > 1;

    public string Text => string.Join(".", Parts);

    public SymbolName IgnoringCase => new SymbolName(Parts, StringComparison.OrdinalIgnoreCase);

    public static SymbolName? Parse(string? text)
    {
      if (string.IsNullOrWhiteSpace(text))
        return null;

      var bare = text!.Trim();
      if (bare.StartsWith("global::", StringComparison.Ordinal))
        bare = bare.Substring("global::".Length);

      // Nested generics strip inside out: Dictionary<string, List<int>> takes two passes.
      for (var previous = ""; previous != bare;)
      {
        previous = bare;
        bare = Decorations.Replace(bare, "");
      }

      var parts = bare.Split('.').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

      return parts.Length == 0 ? null : new SymbolName(parts, StringComparison.Ordinal);
    }

    public bool IsShort(string? name) =>
      string.Equals(name, Short, Comparison);

    /// <summary>
    ///   Whether a fully qualified name ends with this name on a dot boundary: <c>A.B.C</c> matches <c>B.C</c>,
    ///   <c>A.BC</c> does not.
    /// </summary>
    public bool Matches(string qualifiedName)
    {
      var text = Text;
      if (string.Equals(qualifiedName, text, Comparison))
        return true;

      return qualifiedName.Length > text.Length
        && qualifiedName.EndsWith(text, Comparison)
        && qualifiedName[qualifiedName.Length - text.Length - 1] == '.';
    }
  }
}
