using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Bosun.Rclone.Process;

/// <summary>
/// Drains a child process's redirected stdout and stderr for the whole life of the process and
/// forwards a bounded, rate-limited, redacted sample of the lines to the log (bs-0lq).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="Win32RcloneProcessLauncher"/> redirects both streams, and a
/// redirected pipe has a buffer of a few KB. rclone writes its NOTICE and ERROR lines to stderr;
/// once nothing reads the pipe, the next write blocks, <c>rclone rcd</c> stops making progress
/// inside its logger, and rc calls such as <c>mount/unmount</c> can stall behind it while the
/// process is still alive -- so <see cref="RcloneProcessService"/>, which restarts only on exit,
/// never recovers it. <b>Draining is the fix; logging is a by-product.</b>
/// </para>
/// <para>
/// <b>Reading never depends on logging.</b> Every line is consumed whether or not it is logged:
/// the limiter only decides what is written to the log, a throwing logger is swallowed, and a line
/// is bounded while it is assembled (<see cref="MaxReadLineChars"/>) so a stream with no newline
/// cannot grow memory either. A suppressed line costs a counter increment, not a redaction or a
/// log call.
/// </para>
/// <para>
/// <b>Limiter.</b> One budget shared by both streams of a process: at most
/// <see cref="MaxLoggedLinesPerWindow"/> lines per <see cref="Window"/>, then one
/// "rate-limited, N lines not logged" summary, emitted when the next window's first line arrives
/// (or when both streams end), so a chatty or broken rcd costs the rolling log a bounded number of
/// lines per minute. The window is measured with the injected <see cref="TimeProvider"/>'s clock;
/// there is no timer.
/// </para>
/// <para>
/// <b>Credential.</b> Each admitted line goes through <c>redact</c> before it is logged (the
/// launcher passes <see cref="Diagnostics.DiagnosticsRedactor"/> built from the rc credential it is
/// about to put in the child's environment). Redaction runs on the whole bounded line and
/// truncation after it, so a credential cannot be cut in half and leak as a prefix. If redaction
/// throws, the line is dropped rather than logged raw.
/// </para>
/// </remarks>
internal sealed class RcloneOutputPump
{
    /// <summary>Lines logged per <see cref="Window"/>, across stdout and stderr together.</summary>
    public const int MaxLoggedLinesPerWindow = 20;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Longest line text sent to the log; the rest is replaced by a marker.</summary>
    public const int MaxLoggedLineChars = 512;

    /// <summary>Longest line held in memory while assembling it. Larger than
    /// <see cref="MaxLoggedLineChars"/> so redaction sees the whole prefix that can be logged.</summary>
    public const int MaxReadLineChars = 8192;

    private const int ReadBufferChars = 4096;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, string> _redact;
    private readonly string _processLabel;

    private readonly object _limiterGate = new();
    private DateTimeOffset _windowStart;
    private bool _windowOpen;
    private int _loggedInWindow;
    private long _suppressedInWindow;

    /// <param name="logger">The sink.</param>
    /// <param name="time">Clock for the limiter window.</param>
    /// <param name="redact">Applied to every line before it is logged; <see langword="null"/> means
    /// none (only for children that are handed no secret).</param>
    /// <param name="processLabel">Prefix for the log lines, for example the executable's file name.</param>
    public RcloneOutputPump(
        ILogger logger, TimeProvider time, Func<string, string>? redact, string processLabel)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        _logger = logger;
        _time = time;
        _redact = redact ?? (static line => line);
        _processLabel = processLabel;
    }

    /// <summary>
    /// Reads both readers to the end and logs what the limiter allows. Completes when both have
    /// ended (EOF, or a read that fails because the pipe was closed or disposed). Never throws.
    /// </summary>
    public async Task RunAsync(TextReader standardOutput, TextReader standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        await Task.WhenAll(
            DrainAsync(standardOutput, LogLevel.Debug, "stdout"),
            DrainAsync(standardError, LogLevel.Information, "stderr")).ConfigureAwait(false);

        FlushSuppressionSummary();
    }

    private async Task DrainAsync(TextReader reader, LogLevel level, string stream)
    {
        var buffer = new char[ReadBufferChars];
        var line = new StringBuilder();
        long lineLength = 0;

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        HandleLine(line, lineLength, level, stream);
                        line.Clear();
                        lineLength = 0;
                    }
                    else if (c != '\r')
                    {
                        lineLength++;
                        if (line.Length < MaxReadLineChars)
                        {
                            line.Append(c);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // The pipe was closed under us (the handle was disposed, or the child died and its pipe
            // broke) or the reader is otherwise unusable. There is nothing further to drain from it
            // and nobody to tell; one Debug line is enough, and it must itself be unable to throw.
            SafeLog(LogLevel.Debug, ex, "Reading {Process} {Stream} ended with an exception", stream);
        }

        // Last line without a trailing newline (a crash message often has none).
        HandleLine(line, lineLength, level, stream);
    }

    private void HandleLine(StringBuilder line, long lineLength, LogLevel level, string stream)
    {
        if (lineLength == 0)
        {
            return;
        }

        if (!TryAdmit(out var summary))
        {
            return;
        }

        if (summary > 0)
        {
            EmitSummary(summary);
        }

        string text;
        try
        {
            text = _redact(line.ToString());
        }
        catch (Exception)
        {
            // Fail closed: a line we could not redact is not a line we may log.
            return;
        }

        if (lineLength > MaxLoggedLineChars || text.Length > MaxLoggedLineChars)
        {
            var kept = text.Length > MaxLoggedLineChars ? text[..MaxLoggedLineChars] : text;
            text = $"{kept}... [line truncated, {lineLength} chars]";
        }

        SafeLog(level, null, "{Process} {Stream}: {Line}", stream, text);
    }

    /// <summary>Counts the line against the window. Returns <see langword="true"/> when it may be
    /// logged; <paramref name="summary"/> is then the number of lines the previous window
    /// suppressed (to be reported before this line), or 0.</summary>
    private bool TryAdmit(out long summary)
    {
        lock (_limiterGate)
        {
            summary = 0;
            var now = _time.GetUtcNow();

            if (!_windowOpen)
            {
                _windowOpen = true;
                _windowStart = now;
            }
            else if (now - _windowStart >= Window)
            {
                summary = _suppressedInWindow;
                _suppressedInWindow = 0;
                _loggedInWindow = 0;
                _windowStart = now;
            }

            if (_loggedInWindow < MaxLoggedLinesPerWindow)
            {
                _loggedInWindow++;
                return true;
            }

            _suppressedInWindow++;
            return false;
        }
    }

    private void FlushSuppressionSummary()
    {
        long suppressed;
        lock (_limiterGate)
        {
            suppressed = _suppressedInWindow;
            _suppressedInWindow = 0;
        }

        if (suppressed > 0)
        {
            EmitSummary(suppressed);
        }
    }

    private void EmitSummary(long suppressed) =>
        SafeLog(
            LogLevel.Warning,
            null,
            "{Process} output was rate-limited: {Suppressed} lines were read but not logged (limit {Limit} lines per {Window})",
            suppressed,
            MaxLoggedLinesPerWindow,
            Window);

    // Logging is a by-product of draining; a sink that throws must not stop the reads. The
    // arguments are positional, after the always-present {Process}.
    private void SafeLog(LogLevel level, Exception? exception, string template, params object[] args)
    {
        try
        {
            var all = new object[args.Length + 1];
            all[0] = _processLabel;
            Array.Copy(args, 0, all, 1, args.Length);

            if (exception is null)
            {
                _logger.Log(level, template, all);
            }
            else
            {
                _logger.Log(level, exception, template, all);
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed -- see above.
        }
    }
}
