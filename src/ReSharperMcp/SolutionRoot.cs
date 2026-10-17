#nullable enable
using System;
using System.IO;
using System.Linq;
using JetBrains.ProjectModel;

namespace ReSharperMcp
{
  /// <summary>
  ///   Where relative paths start: the solution's directory, and the work tree above it.
  /// </summary>
  /// <remarks>
  ///   A solution often sits below the repository root (TPS: <c>Client/TPS/TPS.All.sln</c>), while agents write
  ///   paths from the root (<c>Shared/Battle/...</c>). Input is tried against both; output is relative to the work tree.
  /// </remarks>
  public sealed class SolutionRoot
  {
    private SolutionRoot(string? directory, string? workspace)
    {
      Directory = directory;
      Workspace = workspace;
    }

    public string? Directory { get; }

    /// <summary>
    ///   The nearest ancestor of <see cref="Directory"/> holding <c>.git</c>, or <see cref="Directory"/> itself.
    /// </summary>
    public string? Workspace { get; }

    public static SolutionRoot Of(ISolution solution) =>
      Of(solution.SolutionFilePath?.Directory?.FullPath);

    public static SolutionRoot Of(string? directory) =>
      new SolutionRoot(directory, WorkTree(directory) ?? directory);

    /// <summary>
    ///   An absolute path for <paramref name="path"/>: the first base it exists under, else under the solution.
    /// </summary>
    /// <returns><c>null</c> when the path is relative and the solution has no directory.</returns>
    public string? Absolute(string path)
    {
      var trimmed = Trimmed(path);
      if (Path.IsPathRooted(trimmed))
        return Path.GetFullPath(trimmed);

      var candidates = new[] { Directory, Workspace }
        .Where(x => x != null)
        .Select(x => Path.GetFullPath(Path.Combine(x!, trimmed)))
        .ToList();

      return candidates.FirstOrDefault(x => File.Exists(x) || System.IO.Directory.Exists(x)) ?? candidates.FirstOrDefault();
    }

    public string Relative(string path)
    {
      var root = Workspace ?? Directory;
      if (root == null || !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        return path;

      return path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
    }

    /// <summary>
    ///   <c>./Shared/A.cs</c> and <c>Shared\A.cs</c> as <c>Shared/A.cs</c>; absolute paths only lose a trailing slash.
    /// </summary>
    public static string Trimmed(string path)
    {
      var normalized = path.Trim().Replace('\\', '/');
      while (normalized.StartsWith("./", StringComparison.Ordinal))
        normalized = normalized.Substring(2);

      return normalized.TrimEnd('/');
    }

    private static string? WorkTree(string? directory)
    {
      for (var current = directory; current != null; current = Path.GetDirectoryName(current))
      {
        var git = Path.Combine(current, ".git");
        if (System.IO.Directory.Exists(git) || File.Exists(git))
          return current;
      }

      return null;
    }
  }
}
