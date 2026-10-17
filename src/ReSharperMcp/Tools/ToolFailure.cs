#nullable enable
using System;
using JetBrains.Util;
using JetBrains.Util.Logging;

namespace ReSharperMcp.Tools
{
  /// <summary>
  ///   A ReSharper crash as one line an agent can act on. The stack trace goes to the IDE log, not the caller.
  /// </summary>
  public static class ToolFailure
  {
    private static readonly ILogger Log = Logger.GetLogger(typeof(ToolFailure));

    /// <summary>
    ///   <see cref="Line"/>, with the stack trace written to the IDE log.
    /// </summary>
    public static string Logged(Exception exception, string subject)
    {
      Log.Warn(exception, ExceptionOrigin.Algorithmic, $"ReSharperMcp failed on {subject}");

      return Line(exception, subject);
    }

    public static string Line(Exception exception, string subject)
    {
      var cause = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
        ? aggregate.InnerExceptions[0]
        : exception;
      var message = FirstLine(cause.Message);

      return $"ReSharper failed on {subject}: {cause.GetType().Name}{(message.Length > 0 ? $": {message}" : "")}";
    }

    private static string FirstLine(string message)
    {
      var end = message.IndexOfAny(new[] { '\r', '\n' });

      return (end < 0 ? message : message.Substring(0, end)).Trim();
    }
  }
}
