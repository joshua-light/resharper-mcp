#nullable enable
using System;
using System.Linq;
using System.Reflection;
using JetBrains.Util;

namespace ReSharperMcp.Disk
{
  /// <summary>
  ///   Rider backend API that the ReSharper SDK does not ship, found in the running process by name.
  /// </summary>
  /// <remarks>
  ///   Each member answers whether it found its target, so a Rider that moved one degrades to the slower path
  ///   instead of failing a call.
  /// </remarks>
  public static class RiderInternals
  {
    private static readonly Lazy<MethodInfo?> PathToRd = new Lazy<MethodInfo?>(() =>
      Type("JetBrains.Platform.Ide", "JetBrains.IDE.PathExtensions")
        ?.GetMethod("ToRd", new[] { typeof(VirtualFileSystemPath) }));

    /// <summary>
    ///   <c>JetBrains.Rider.Model.RdPath</c> for 2026.x protocol models; <c>null</c> where it does not exist.
    /// </summary>
    public static object? RdPath(VirtualFileSystemPath path) =>
      PathToRd.Value == null ? null : Invoke(PathToRd.Value, path);

    private static object Invoke(MethodInfo method, params object[] arguments)
    {
      try
      {
        return method.Invoke(null, arguments);
      }
      catch (TargetInvocationException exception) when (exception.InnerException != null)
      {
        throw exception.InnerException;
      }
    }

    private static Type? Type(string assembly, string type) =>
      AppDomain.CurrentDomain.GetAssemblies()
        .Where(x => x.GetName().Name == assembly)
        .Select(x => x.GetType(type))
        .FirstOrDefault(x => x != null);
  }
}
