using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Support;

/// <summary>
/// <see cref="ILogger{TCategoryName}"/> that records level, rendered message, and exception for
/// every call, so a test can assert that something was logged (and at what severity) rather than
/// trusting that a <c>catch</c> block's log line exists. Thread-safe: the supervisor's real
/// <c>RunAsync</c> loop logs from a thread-pool thread while the test thread reads.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> entries = new();

    public IReadOnlyList<LogEntry> Entries => entries.ToArray();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }
}
