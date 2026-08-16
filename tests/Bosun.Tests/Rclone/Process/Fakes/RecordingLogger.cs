using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Rclone.Process.Fakes;

/// <summary>
/// Captures the fully-rendered text of every log call -- the formatted message, the exception's
/// full <see cref="Exception.ToString"/> (message + stack trace), AND every individual structured
/// property value -- so a test can assert a secret never reaches ANY of the surfaces a real
/// Serilog sink would render it to (bs-ard). Deliberately broader than just the formatted
/// message: capturing structured property values too means this still catches a leak even if a
/// future call site logs the secret as a bare structured parameter that happens not to appear in
/// the message template text itself.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> CapturedText { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        CapturedText.Add(formatter(state, exception));

        if (exception is not null)
        {
            CapturedText.Add(exception.ToString());
        }

        if (state is IEnumerable<KeyValuePair<string, object?>> structuredProperties)
        {
            foreach (var (_, value) in structuredProperties)
            {
                if (value is not null)
                {
                    CapturedText.Add(value.ToString() ?? string.Empty);
                }
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose()
        {
        }
    }
}
