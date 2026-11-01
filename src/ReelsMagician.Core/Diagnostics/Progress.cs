namespace ReelsMagician.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Stage, string Message);

/// <summary>
/// Sink for pipeline progress. Implementations must be safe to call from worker threads;
/// the WPF implementation marshals to the dispatcher.
/// </summary>
public interface IProgressSink
{
    void Log(LogLevel level, string stage, string message);

    /// <param name="fraction">0..1, or null when the work is indeterminate.</param>
    void Report(string stage, string message, double? fraction);
}

public sealed class NullProgressSink : IProgressSink
{
    public static readonly NullProgressSink Instance = new();
    public void Log(LogLevel level, string stage, string message) { }
    public void Report(string stage, string message, double? fraction) { }
}

public sealed class DelegateProgressSink(
    Action<LogEntry> onLog,
    Action<string, string, double?> onReport) : IProgressSink
{
    public void Log(LogLevel level, string stage, string message)
        => onLog(new LogEntry(DateTimeOffset.Now, level, stage, message));

    public void Report(string stage, string message, double? fraction)
        => onReport(stage, message, fraction);
}

public static class ProgressSinkExtensions
{
    public static void Info(this IProgressSink s, string stage, string message) => s.Log(LogLevel.Info, stage, message);
    public static void Warn(this IProgressSink s, string stage, string message) => s.Log(LogLevel.Warn, stage, message);
    public static void Error(this IProgressSink s, string stage, string message) => s.Log(LogLevel.Error, stage, message);
    public static void Debug(this IProgressSink s, string stage, string message) => s.Log(LogLevel.Debug, stage, message);
}
