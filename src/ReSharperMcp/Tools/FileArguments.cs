#nullable enable
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
  /// <summary>
  ///   The files a tool call names, by the argument names every tool shares: <c>filePath</c>, <c>filePaths</c> and
  ///   <c>symbols[].filePath</c>.
  /// </summary>
  public static class FileArguments
  {
    public static IReadOnlyList<string> Of(JObject arguments) =>
      Tokens(arguments)
        .Select(x => x?.Type == JTokenType.String ? x.ToString() : null)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x!)
        .Distinct()
        .ToList();

    /// <summary>
    ///   The named files as absolute paths; relative ones are taken from <paramref name="root"/>.
    /// </summary>
    public static IReadOnlyList<string> Absolute(JObject arguments, SolutionRoot root) =>
      Of(arguments)
        .Select(root.Absolute)
        .Where(x => x != null)
        .Select(x => x!)
        .Distinct()
        .ToList();

    private static IEnumerable<JToken?> Tokens(JObject arguments)
    {
      yield return arguments["filePath"];

      if (arguments["filePaths"] is JArray paths)
      {
        foreach (var path in paths)
          yield return path;
      }

      if (arguments["symbols"] is JArray symbols)
      {
        foreach (var symbol in symbols.OfType<JObject>())
          yield return symbol["filePath"];
      }
    }
  }
}
