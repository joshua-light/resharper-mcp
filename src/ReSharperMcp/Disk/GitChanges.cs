#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReSharperMcp.Disk
{
  /// <summary>
  ///   Files git reports as changed in a working tree: modified since a ref (committed or not) plus untracked ones.
  /// </summary>
  public static class GitChanges
  {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <returns>Absolute paths of changed files that still exist.</returns>
    /// <exception cref="InvalidOperationException">Git is missing, the directory is not a work tree, or the ref is unknown.</exception>
    public static IReadOnlyList<string> Since(string directory, string reference)
    {
      var root = Git(directory, "rev-parse", "--show-toplevel").Single();
      var changed = Git(root, "diff", "--name-only", "--diff-filter=d", reference, "--");
      var untracked = Git(root, "ls-files", "--others", "--exclude-standard");

      return changed.Concat(untracked)
        .Select(x => Path.GetFullPath(Path.Combine(root, x)))
        .Where(File.Exists)
        .Distinct()
        .ToList();
    }

    private static IReadOnlyList<string> Git(string directory, params string[] arguments)
    {
      var start = new ProcessStartInfo("git", string.Join(" ", arguments.Select(Quoted)))
      {
        WorkingDirectory = directory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };

      try
      {
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEndAsync();
        if (!git.WaitForExit((int) Timeout.TotalMilliseconds))
        {
          git.Kill();
          throw new InvalidOperationException($"git {arguments[0]} timed out after {Timeout.TotalSeconds}s");
        }

        if (git.ExitCode != 0)
          throw new InvalidOperationException($"git {arguments[0]} failed: {error.Result.Trim()}");

        return output.Result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
      }
      catch (System.ComponentModel.Win32Exception exception)
      {
        throw new InvalidOperationException($"git is not available: {exception.Message}");
      }
    }

    private static string Quoted(string argument) =>
      argument.Any(char.IsWhiteSpace) || argument.Contains('"')
        ? $"\"{argument.Replace("\"", "\\\"")}\""
        : argument;
  }
}
