#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Util;
using JetBrains.Util.Logging;

namespace ReSharperMcp.Disk
{
  /// <summary>
  ///   Source files that changed on disk since they were last taken, collected by watching directories.
  /// </summary>
  /// <remarks>
  ///   Rider notices external edits by itself, but late while its window is in the background. This list is what
  ///   lets a tool call that names no file still catch up with edits made by <c>sed</c>, scripts or heredocs.
  ///   Only directories that are watched are covered; files a call names are checked regardless.
  /// </remarks>
  public sealed class DiskChanges : IDisposable
  {
    private static readonly ILogger Log = Logger.GetLogger<DiskChanges>();

    private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      ".cs",
      ".fs",
      ".fsi",
      ".fsx",
      ".vb",
      ".razor",
      ".cshtml",
    };

    private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "bin",
      "obj",
      ".git",
      ".idea",
      ".vs",
      "node_modules",
      "Library",
      "Temp",
      "Logs",
    };

    private readonly object _lock = new object();
    private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
    private readonly List<string> _roots = new List<string>();
    private readonly HashSet<string> _paths = new HashSet<string>(StringComparer.Ordinal);
    private bool _overflowed;

    public int Count
    {
      get
      {
        lock (_lock)
          return _overflowed ? _paths.Count + 1 : _paths.Count;
      }
    }

    /// <summary>
    ///   Starts watching <paramref name="root"/> recursively, unless a watched root already covers it.
    /// </summary>
    public void Watch(string root)
    {
      lock (_lock)
      {
        if (!Directory.Exists(root) || _roots.Any(x => Covers(x, root)))
          return;

        _roots.Add(root);
      }

      try
      {
        var watcher = new FileSystemWatcher(root)
        {
          IncludeSubdirectories = true,
          NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
          InternalBufferSize = 64 * 1024,
        };
        // Watcher events arrive on a runtime thread where an unhandled exception terminates Rider's backend.
        watcher.Changed += (_, e) => Safely(() => Add(root, e.FullPath));
        watcher.Created += (_, e) => Safely(() => Add(root, e.FullPath));
        watcher.Deleted += (_, e) => Safely(() => Add(root, e.FullPath));
        watcher.Renamed += (_, e) => Safely(() =>
        {
          Add(root, e.OldFullPath);
          Add(root, e.FullPath);
        });
        watcher.Error += (_, e) => Safely(() => Overflow(e.GetException()));
        watcher.EnableRaisingEvents = true;

        lock (_lock)
          _watchers.Add(watcher);
      }
      catch (Exception exception)
      {
        Log.Warn(exception, ExceptionOrigin.Algorithmic, $"ReSharperMcp cannot watch '{root}'; only files named by a call will be checked");
      }
    }

    /// <summary>
    ///   Takes the changed files, or the watched roots when the watcher lost track of events.
    /// </summary>
    public DiskChangeSet Take()
    {
      lock (_lock)
      {
        var set = new DiskChangeSet(_paths.ToList(), _overflowed ? _roots.ToList() : new List<string>());
        _paths.Clear();
        _overflowed = false;

        return set;
      }
    }

    public void Dispose()
    {
      lock (_lock)
      {
        foreach (var watcher in _watchers)
          watcher.Dispose();

        _watchers.Clear();
      }
    }

    private void Safely(Action handle)
    {
      try
      {
        handle();
      }
      catch (Exception exception)
      {
        try
        {
          Overflow(exception);
        }
        catch
        {
          // Nothing may escape a watcher thread, not even a failure to log.
        }
      }
    }

    private void Add(string root, string path)
    {
      if (!Extensions.Contains(Path.GetExtension(path)) || IsIgnored(root, path))
        return;

      lock (_lock)
        _paths.Add(path);
    }

    private void Overflow(Exception exception)
    {
      Log.Warn(exception, ExceptionOrigin.Algorithmic, "ReSharperMcp lost file change events; the next call refreshes the watched roots");
      lock (_lock)
        _overflowed = true;
    }

    private static bool IsIgnored(string root, string path)
    {
      var relative = path.Length > root.Length ? path.Substring(root.Length) : "";
      var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
        StringSplitOptions.RemoveEmptyEntries);

      return segments.Take(segments.Length - 1).Any(IgnoredDirectories.Contains);
    }

    private static bool Covers(string root, string path)
    {
      var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

      return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
  }

  /// <summary>
  ///   What <see cref="DiskChanges.Take"/> returned: files that changed, and directories to rescan whole when
  ///   change events were lost.
  /// </summary>
  public sealed class DiskChangeSet
  {
    public DiskChangeSet(IReadOnlyList<string> files, IReadOnlyList<string> directories) =>
      (Files, Directories) = (files, directories);

    public IReadOnlyList<string> Files { get; }

    public IReadOnlyList<string> Directories { get; }
  }
}
