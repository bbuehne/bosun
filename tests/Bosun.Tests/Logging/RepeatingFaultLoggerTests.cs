using Bosun.Logging;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.RcTimeout;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Logging;

/// <summary>
/// bs-qcs: the throttle that stops a fault repeating on a timer from filling the log. Injected
/// time only; nothing here waits.
/// </summary>
public sealed class RepeatingFaultLoggerTests
{
    private static readonly TimeSpan Reminder = TimeSpan.FromMinutes(10);

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));

    private RepeatingFaultLogger Create() => new(time, Reminder);

    [Fact]
    public void The_first_sighting_is_logged()
    {
        var seen = Create().Observe("rcd", "HTTP 401");

        Assert.Equal(FaultLogAction.Log, seen.Action);
        Assert.Equal(1, seen.Count);
    }

    [Fact]
    public void Repeats_of_the_same_kind_inside_the_interval_are_suppressed()
    {
        var faults = Create();
        faults.Observe("rcd", "HTTP 401");

        for (var i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(FaultLogAction.Suppress, faults.Observe("rcd", "HTTP 401").Action);
        }
    }

    [Fact]
    public void A_reminder_comes_after_the_interval_with_the_running_count_and_the_start_time()
    {
        var faults = Create();
        var started = time.GetLocalNow();
        faults.Observe("rcd", "HTTP 401");
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            faults.Observe("rcd", "HTTP 401"); // suppressed: 5 sightings so far
        }

        time.Advance(Reminder);
        var seen = faults.Observe("rcd", "HTTP 401");

        Assert.Equal(FaultLogAction.Remind, seen.Action);
        Assert.Equal(6, seen.Count);
        Assert.Equal(started, seen.Since);
    }

    [Fact]
    public void After_a_reminder_the_next_one_is_a_full_interval_away()
    {
        var faults = Create();
        faults.Observe("rcd", "HTTP 401");
        time.Advance(Reminder);
        Assert.Equal(FaultLogAction.Remind, faults.Observe("rcd", "HTTP 401").Action);

        time.Advance(Reminder - TimeSpan.FromSeconds(1));
        Assert.Equal(FaultLogAction.Suppress, faults.Observe("rcd", "HTTP 401").Action);

        time.Advance(TimeSpan.FromSeconds(1));
        var next = faults.Observe("rcd", "HTTP 401");
        Assert.Equal(FaultLogAction.Remind, next.Action);
        Assert.Equal(4, next.Count);
    }

    [Fact]
    public void A_change_of_kind_is_logged_at_once_and_starts_a_new_count()
    {
        var faults = Create();
        faults.Observe("rcd", "HTTP 401");
        faults.Observe("rcd", "HTTP 401");

        time.Advance(TimeSpan.FromSeconds(5));
        var seen = faults.Observe("rcd", "connection refused");

        Assert.Equal(FaultLogAction.Log, seen.Action);
        Assert.Equal(1, seen.Count);
        Assert.Equal(time.GetLocalNow(), seen.Since);
        Assert.Equal(FaultLogAction.Suppress, faults.Observe("rcd", "connection refused").Action);
    }

    [Fact]
    public void Recovery_reports_the_count_once_and_the_fault_is_then_new_again()
    {
        var faults = Create();
        faults.Observe("rcd", "HTTP 401");
        faults.Observe("rcd", "HTTP 401");
        faults.Observe("rcd", "HTTP 401");

        var ended = faults.Recover("rcd");

        Assert.NotNull(ended);
        Assert.Equal(3, ended.Value.Count);
        Assert.Null(faults.Recover("rcd"));
        Assert.Equal(FaultLogAction.Log, faults.Observe("rcd", "HTTP 401").Action);
    }

    [Fact]
    public void Recovering_something_that_was_not_failing_reports_nothing()
    {
        Assert.Null(Create().Recover("rcd"));
    }

    [Fact]
    public void Sources_are_independent()
    {
        var faults = Create();
        faults.Observe("host-a", "HTTP 401");
        faults.Observe("host-a", "HTTP 401");

        Assert.Equal(FaultLogAction.Log, faults.Observe("host-b", "HTTP 401").Action);
        Assert.Equal(FaultLogAction.Suppress, faults.Observe("host-a", "HTTP 401").Action);
        Assert.Equal(1, faults.Recover("host-b")!.Value.Count);
        Assert.Equal(3, faults.Recover("host-a")!.Value.Count);
    }

    // -- the logging extension: what actually reaches the log --------------------------------

    [Fact]
    public void The_extension_writes_the_message_then_one_reminder_per_interval_then_one_recovery()
    {
        var faults = Create();
        var log = new LevelRecordingLogger<RepeatingFaultLoggerTests>();

        for (var i = 0; i < 100; i++)
        {
            log.LogRepeatingFault(
                faults, "rcd", "HTTP 401", LogLevel.Error, null, "rclone rcd did not become healthy: HTTP 401",
                "rclone rcd start", "HTTP 401", "attempts");
            time.Advance(TimeSpan.FromSeconds(20)); // 100 attempts span 33 minutes
        }

        log.LogFaultRecovered(faults, "rcd", "rclone rcd start", "attempts");

        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        var reminders = log.Entries.Where(e => e.Message.Contains("still failing", StringComparison.Ordinal)).ToList();
        var recoveries = log.Entries.Where(e => e.Level == LogLevel.Information).ToList();

        Assert.Single(errors);
        Assert.Equal(3, reminders.Count); // at 10, 20 and 30 minutes
        Assert.All(reminders, r => Assert.Equal(LogLevel.Warning, r.Level));
        Assert.Contains("rclone rcd start: still failing: HTTP 401 (31 attempts since", reminders[0].Message, StringComparison.Ordinal);
        Assert.Single(recoveries);
        Assert.Contains("working again after 100 failed attempts", recoveries[0].Message, StringComparison.Ordinal);
    }
}
