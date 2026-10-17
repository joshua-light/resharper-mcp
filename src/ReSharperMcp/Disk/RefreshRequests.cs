#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Rider.Model;
using JetBrains.Util;

namespace ReSharperMcp.Disk
{
  /// <summary>
  ///   Builds <see cref="RdFsRefreshRequest"/>s on every supported Rider.
  /// </summary>
  /// <remarks>
  ///   The request takes <c>List&lt;string&gt;</c> in 2025.3 (the SDK compiled against) and <c>List&lt;RdPath&gt;</c>
  ///   in 2026.x, so its constructor is found at run time rather than bound at compile time.
  /// </remarks>
  public static class RefreshRequests
  {
    public static RdFsRefreshRequest New(IEnumerable<string> paths)
    {
      var constructor = typeof(RdFsRefreshRequest).GetConstructors().Single(x => x.GetParameters().Length == 2);
      var listType = constructor.GetParameters()[0].ParameterType;
      var list = (IList) Activator.CreateInstance(listType);
      var toElement = Element(listType.GetGenericArguments()[0]);
      foreach (var path in paths)
        list.Add(toElement(path));

      return (RdFsRefreshRequest) constructor.Invoke(new object[] { list, false });
    }

    private static Func<string, object> Element(Type elementType)
    {
      if (elementType == typeof(string))
        return path => path;

      return path => RiderInternals.RdPath(VirtualFileSystemPath.Parse(path, InteractionContext.SolutionContext))
        ?? throw new MissingMethodException($"No conversion from a path to {elementType.FullName}");
    }
  }
}
