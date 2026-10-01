using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>One captured log call.</summary>
internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Captures each log call's level, rendered message and exception object. The existing
/// <c>RecordingLogger</c> keeps only rendered text, and bs-x57's acceptance criterion ("is
/// logged") needs the level and the exception. Locked because in <c>RunAsync</c> tests the
/// supervisor logs from its loop thread.
/// </summary>
internal sealed class LevelRecordingLogger<T> : ILogger<T>
{
    private readonly object gate = new();
    private readonly List<LogEntry> entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (gate)
        {
            entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
