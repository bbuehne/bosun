using System.IO;
using Bosun.Diagnostics;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// bs-0lq: the pump that keeps a redirected <c>rclone rcd</c> from blocking on a full pipe. Pure
/// in-memory readers, a fake clock and a recording logger -- no process, no pipe. The one test that
/// proves a real child can no longer block lives in
/// <see cref="Win32RcloneProcessLauncherIntegrationTests"/>.
/// </summary>
public sealed class RcloneOutputPumpTests
{
    private static readonly string Password = "Zq9pAssW0rd-ofTheRcCredential";

    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLogger _log = new();

    private RcloneOutputPump NewPump(Func<string, string>? redact = null) =>
        new(_log, _time, redact, "rclone.exe");

    private static string Lines(string prefix, int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix}{i}\n"));

    // ---- reading never depends on logging ----

    [Fact]
    public async Task Every_line_is_consumed_even_while_the_limiter_is_suppressing()
    {
        var stderr = new ScriptedReader(Lines("err-", 500));
        var stdout = new ScriptedReader(Lines("out-", 500));

        await NewPump().RunAsync(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stderr.ReachedEnd);
        Assert.True(stdout.ReachedEnd);
        Assert.Equal(Lines("err-", 500).Length, stderr.CharsHandedOut);
        Assert.Equal(Lines("out-", 500).Length, stdout.CharsHandedOut);

        // 1000 lines read; 20 logged; one summary for the other 980.
        Assert.Equal(RcloneOutputPump.MaxLoggedLinesPerWindow, _log.Lines.Count);
        var summary = Assert.Single(_log.Summaries);
        Assert.Equal(1000 - RcloneOutputPump.MaxLoggedLinesPerWindow, SuppressedCount(summary));
    }

    [Fact]
    public async Task A_line_within_the_budget_logs_and_no_summary_appears()
    {
        await NewPump().RunAsync(new ScriptedReader(""), new ScriptedReader("one\ntwo\nthree\n"));

        Assert.Equal(3, _log.Lines.Count);
        Assert.Empty(_log.Summaries);
    }

    [Fact]
    public async Task Suppressed_lines_are_not_redacted_so_suppression_costs_a_counter_not_a_regex()
    {
        var redactCalls = 0;
        var pump = NewPump(line =>
        {
            redactCalls++;
            return line;
        });

        await pump.RunAsync(new ScriptedReader(""), new ScriptedReader(Lines("e-", 300)));

        Assert.Equal(RcloneOutputPump.MaxLoggedLinesPerWindow, redactCalls);
    }

    // ---- the limiter window ----

    [Fact]
    public async Task The_summary_appears_once_per_window_with_that_windows_count()
    {
        var stderr = new ScriptedReader(
            Lines("w1-", 25), // window 1: 20 logged, 5 suppressed
            (Action)(() => _time.Advance(RcloneOutputPump.Window + TimeSpan.FromSeconds(1))),
            Lines("w2-", 27)); // window 2: 20 logged, 7 suppressed

        await NewPump().RunAsync(new ScriptedReader(""), stderr);

        Assert.Equal(40, _log.Lines.Count);
        Assert.Equal(2, _log.Summaries.Count);
        Assert.Equal(5, SuppressedCount(_log.Summaries[0]));
        Assert.Equal(7, SuppressedCount(_log.Summaries[1]));

        // Window 1's summary is reported before window 2's first line, not after it.
        var order = _log.Entries.Select(e => e.Message).ToList();
        var firstSummary = order.FindIndex(m => m.Contains("rate-limited", StringComparison.Ordinal));
        Assert.Equal(20, firstSummary);
        Assert.Contains("w2-1", order[firstSummary + 1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_budget_is_not_renewed_before_the_window_has_elapsed()
    {
        var stderr = new ScriptedReader(
            Lines("a-", 20),
            (Action)(() => _time.Advance(RcloneOutputPump.Window - TimeSpan.FromSeconds(1))),
            Lines("b-", 10));

        await NewPump().RunAsync(new ScriptedReader(""), stderr);

        Assert.Equal(20, _log.Lines.Count);
        Assert.Equal(10, SuppressedCount(Assert.Single(_log.Summaries)));
    }

    [Fact]
    public async Task The_budget_is_shared_by_stdout_and_stderr()
    {
        await NewPump().RunAsync(new ScriptedReader(Lines("o-", 20)), new ScriptedReader(Lines("e-", 20)));

        Assert.Equal(RcloneOutputPump.MaxLoggedLinesPerWindow, _log.Lines.Count);
        Assert.Equal(20, SuppressedCount(Assert.Single(_log.Summaries)));
    }

    // ---- shape of what is logged ----

    [Fact]
    public async Task Stderr_is_logged_at_Information_and_stdout_at_Debug()
    {
        await NewPump().RunAsync(new ScriptedReader("from-out\n"), new ScriptedReader("from-err\n"));

        Assert.Equal(LogLevel.Debug, Assert.Single(_log.Lines, e => e.Message.Contains("from-out", StringComparison.Ordinal)).Level);
        Assert.Equal(LogLevel.Information, Assert.Single(_log.Lines, e => e.Message.Contains("from-err", StringComparison.Ordinal)).Level);
    }

    [Fact]
    public async Task A_very_long_line_is_truncated_and_says_how_long_it_was()
    {
        var longLine = new string('x', 5_000);

        await NewPump().RunAsync(new ScriptedReader(""), new ScriptedReader(longLine + "\n"));

        var entry = Assert.Single(_log.Lines);
        Assert.Contains("[line truncated, 5000 chars]", entry.Message, StringComparison.Ordinal);
        Assert.True(entry.Message.Length < RcloneOutputPump.MaxLoggedLineChars + 200);
        Assert.DoesNotContain(new string('x', RcloneOutputPump.MaxLoggedLineChars + 1), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_huge_stream_with_no_newline_is_fully_read_and_logs_one_bounded_line()
    {
        // 3 MB in 64 KB chunks, never a newline: nothing may buffer it all, and every char is read.
        var chunk = new string('y', 65_536);
        var steps = Enumerable.Repeat((object)chunk, 48).ToArray();
        var stderr = new ScriptedReader(steps);

        await NewPump().RunAsync(new ScriptedReader(""), stderr);

        Assert.Equal(48L * 65_536, stderr.CharsHandedOut);
        var entry = Assert.Single(_log.Lines);
        Assert.Contains($"[line truncated, {48 * 65_536} chars]", entry.Message, StringComparison.Ordinal);
        Assert.True(entry.Message.Length < RcloneOutputPump.MaxLoggedLineChars + 200);
    }

    [Fact]
    public async Task A_last_line_without_a_newline_is_logged_blank_lines_are_not_and_CRLF_is_handled()
    {
        await NewPump().RunAsync(new ScriptedReader(""), new ScriptedReader("first\r\n\r\n\nsecond\r\nno-newline-at-end"));

        Assert.Collection(
            _log.Lines,
            e => Assert.EndsWith("first", e.Message, StringComparison.Ordinal),
            e => Assert.EndsWith("second", e.Message, StringComparison.Ordinal),
            e => Assert.EndsWith("no-newline-at-end", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_split_across_reads_is_reassembled()
    {
        await NewPump().RunAsync(new ScriptedReader(""), new ScriptedReader("NOTICE: mou", "nt ready\nsecond"));

        Assert.Collection(
            _log.Lines,
            e => Assert.EndsWith("NOTICE: mount ready", e.Message, StringComparison.Ordinal),
            e => Assert.EndsWith("second", e.Message, StringComparison.Ordinal));
    }

    // ---- failure and completion ----

    [Fact]
    public async Task A_reader_that_throws_does_not_crash_the_pump_and_the_other_stream_is_still_drained()
    {
        var failing = new ScriptedReader("before-the-fault\n", new IOException("pipe closed"), "never-seen\n");
        var healthy = new ScriptedReader(Lines("ok-", 5));

        await NewPump().RunAsync(healthy, failing).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(healthy.ReachedEnd);
        Assert.Contains(_log.Lines, e => e.Message.Contains("before-the-fault", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, e => e.Message.Contains("never-seen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reader_that_ends_at_once_is_not_an_error()
    {
        await NewPump().RunAsync(new ScriptedReader(), new ScriptedReader()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task A_disposed_reader_is_not_an_error()
    {
        var disposed = new StringReader("x");
        disposed.Dispose();

        await NewPump().RunAsync(new ScriptedReader("fine\n"), disposed).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(_log.Lines, e => e.Message.Contains("fine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pump_completes_only_when_both_readers_have_ended()
    {
        var stdout = new GatedReader();
        var stderr = new GatedReader();
        var run = NewPump().RunAsync(stdout, stderr);

        stdout.Feed("out\n");
        stderr.Feed("err\n");
        stdout.End();
        await stdout.Ended;
        Assert.False(run.IsCompleted, "stderr is still open");

        stderr.End();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _log.Lines.Count);
    }

    [Fact]
    public async Task A_sink_that_throws_does_not_stop_the_reading()
    {
        _log.ThrowOnLog = true;
        var stderr = new ScriptedReader(Lines("e-", 200));
        var stdout = new ScriptedReader(Lines("o-", 200));

        await NewPump().RunAsync(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stderr.ReachedEnd);
        Assert.True(stdout.ReachedEnd);
    }

    // ---- the credential ----

    [Fact]
    public async Task A_line_containing_the_credential_never_reaches_the_log_in_any_form()
    {
        var credential = new RcloneRcCredential(RcloneRcCredential.DefaultUserName, Password);
        var redactor = new DiagnosticsRedactor(credential);
        var stderr = new ScriptedReader(
            $"ERROR : auth failed for bosun:{Password}\n" +
            $"NOTICE: env RCLONE_RC_PASS={Password}\n" +
            $"ERROR : header {{\"Authorization\":\"{credential.ToBasicAuthHeaderValue()}\"}}\n" +
            $"url http://bosun:{Uri.EscapeDataString(Password)}@127.0.0.1:5572/\n");

        await NewPump(redactor.Redact).RunAsync(new ScriptedReader(""), stderr);

        Assert.Equal(4, _log.Lines.Count);
        Assert.DoesNotContain(_log.AllText, t => t.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.AllText, t => t.Contains(Uri.EscapeDataString(Password), StringComparison.Ordinal));
        Assert.DoesNotContain(_log.AllText, t => t.Contains(credential.ToBasicAuthHeaderValue(), StringComparison.Ordinal));
        Assert.Contains(_log.Lines, e => e.Message.Contains(DiagnosticsRedactor.Mask, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_credential_that_straddles_the_truncation_point_is_masked_not_cut_in_half()
    {
        // Redaction runs on the whole bounded line before truncation: truncating first would leave
        // the first half of the secret as the tail of the logged text.
        var redactor = new DiagnosticsRedactor(new RcloneRcCredential(RcloneRcCredential.DefaultUserName, Password));
        var line = new string('.', RcloneOutputPump.MaxLoggedLineChars - 8) + Password + new string('.', 100);

        await NewPump(redactor.Redact).RunAsync(new ScriptedReader(""), new ScriptedReader(line + "\n"));

        var entry = Assert.Single(_log.Lines);
        Assert.DoesNotContain(Password[..6], string.Concat(_log.AllText), StringComparison.Ordinal);
        Assert.Contains("truncated", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_the_redactor_throws_on_is_dropped_not_logged_raw_and_reading_continues()
    {
        var pump = NewPump(line => line.Contains("poison", StringComparison.Ordinal)
            ? throw new InvalidOperationException("redactor failure")
            : line);
        var stderr = new ScriptedReader("poison " + Password + "\nclean line\n");

        await pump.RunAsync(new ScriptedReader(""), stderr);

        Assert.True(stderr.ReachedEnd);
        Assert.Single(_log.Lines);
        Assert.Contains("clean line", _log.Lines[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.AllText, t => t.Contains(Password, StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static long SuppressedCount(Entry summary) =>
        (long)summary.Args.Single(a => a is long)!;

    private sealed record Entry(LogLevel Level, string Message, IReadOnlyList<object?> Args);

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<Entry> _entries = [];

        public bool ThrowOnLog { get; set; }

        public List<Entry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public List<Entry> Lines => [.. Entries.Where(e => !IsSummary(e))];

        public List<Entry> Summaries => [.. Entries.Where(IsSummary)];

        /// <summary>Every rendered string and every structured value, so a leak through a bare
        /// structured argument is caught as well as one in the message text.</summary>
        public List<string> AllText =>
            [.. Entries.SelectMany(e => e.Args.Select(a => a?.ToString() ?? string.Empty).Append(e.Message))];

        private static bool IsSummary(Entry e) => e.Message.Contains("rate-limited", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog)
            {
                throw new InvalidOperationException("sink failure");
            }

            var args = state is IEnumerable<KeyValuePair<string, object?>> kv
                ? kv.Where(p => p.Key != "{OriginalFormat}").Select(p => p.Value).ToList()
                : [];

            lock (_gate)
            {
                _entries.Add(new Entry(logLevel, formatter(state, exception), args));
            }
        }
    }

    /// <summary>Hands out scripted chunks: a string is a chunk, an <see cref="Action"/> runs
    /// between reads (to move the fake clock), an <see cref="Exception"/> is thrown from the read.
    /// Large chunks are split to the caller's buffer size, like a real stream.</summary>
    private sealed class ScriptedReader : TextReader
    {
        private readonly Queue<object> _steps;
        private string _current = string.Empty;
        private int _offset;

        public ScriptedReader(params object[] steps) => _steps = new Queue<object>(steps);

        public long CharsHandedOut { get; private set; }

        public bool ReachedEnd { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (_steps.Count == 0)
                {
                    ReachedEnd = true;
                    return ValueTask.FromResult(0);
                }

                switch (_steps.Dequeue())
                {
                    case string chunk:
                        _current = chunk;
                        _offset = 0;
                        break;
                    case Action action:
                        action();
                        break;
                    case Exception ex:
                        throw ex;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            CharsHandedOut += count;
            return ValueTask.FromResult(count);
        }
    }

    /// <summary>A reader that returns nothing until told to: the shape of a live pipe.</summary>
    private sealed class GatedReader : TextReader
    {
        private readonly object _gate = new();
        private readonly Queue<string> _fed = new();
        private TaskCompletionSource _signal = NewSignal();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _end;
        private string _current = string.Empty;
        private int _offset;

        public Task Ended => _ended.Task;

        public void Feed(string text) => Signal(() => _fed.Enqueue(text));

        public void End() => Signal(() => _end = true);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_offset >= _current.Length && _fed.Count > 0)
                    {
                        _current = _fed.Dequeue();
                        _offset = 0;
                    }

                    if (_offset < _current.Length)
                    {
                        var count = Math.Min(buffer.Length, _current.Length - _offset);
                        _current.AsMemory(_offset, count).CopyTo(buffer);
                        _offset += count;
                        return count;
                    }

                    if (_end)
                    {
                        _ended.TrySetResult();
                        return 0;
                    }

                    wait = _signal.Task;
                }

                await wait;
            }
        }

        private void Signal(Action change)
        {
            TaskCompletionSource old;
            lock (_gate)
            {
                change();
                old = _signal;
                _signal = NewSignal();
            }

            old.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
