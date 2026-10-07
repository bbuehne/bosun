using System.Net.Http;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.Supervisor.Independent.RcTimeout;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// bs-qcs: a dead or foreign rclone must not turn the log into 200 MB a day. On 2026-10-04..06 an
/// orphaned rcd answered every call with 401; the health wait logged each failed poll (about every
/// 250 ms) with a stack trace, and the supervise loop repeated the whole attempt every 5 s. These
/// count what reaches the logger. Fakes and injected time only.
/// </summary>
public sealed class RcloneProcessLogVolumeTests
{
    private static RcloneRcException Unauthorized() =>
        new("core/version", 401, "rc call to 'core/version' failed: unauthorized");

    [Fact]
    public async Task A_15_second_health_wait_of_60_polls_that_all_get_401_logs_two_entries_and_none_carries_the_exception()
    {
        var f = new Fixture();
        f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        for (var i = 0; i < 200; i++)
        {
            f.Client.EnqueueVersionFailure(Unauthorized());
        }

        var start = f.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilCompleteAsync(f.Time, start, TimeSpan.FromMilliseconds(250));
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(f.Client.GetVersionCalls.Count >= 59, $"expected about 60 polls, saw {f.Client.GetVersionCalls.Count}");
        Assert.Equal(RcloneProcessFaultKind.HealthCheckUnauthorized, f.Service.FaultKind);

        var entries = f.Log.Entries;
        Assert.Equal(2, entries.Count); // the first failed poll, and the attempt's Error
        Assert.All(entries, e => Assert.Null(e.Exception));

        var error = Assert.Single(entries, e => e.Level == LogLevel.Error);
        Assert.Contains("HTTP 401", error.Message, StringComparison.Ordinal);
        Assert.Contains("another rclone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_kind_of_poll_failure_within_one_attempt_is_logged_and_only_an_unexpected_type_keeps_its_stack()
    {
        var f = new Fixture();
        f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        f.Client.EnqueueVersionFailure(Unauthorized());
        f.Client.EnqueueVersionFailure(Unauthorized());
        f.Client.EnqueueVersionFailure(new HttpRequestException("actively refused"));
        f.Client.EnqueueVersionFailure(new HttpRequestException("actively refused"));
        f.Client.EnqueueVersionFailure(new InvalidOperationException("something nobody planned for"));
        f.Client.EnqueueVersionFailure(new InvalidOperationException("something nobody planned for"));
        // Then the queue is empty and the next poll succeeds.

        var start = f.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilCompleteAsync(f.Time, start, TimeSpan.FromMilliseconds(250));
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        var failures = f.Log.Entries.Where(e => e.Message.Contains("health check attempt failed", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, failures.Count);
        Assert.Contains("HTTP 401", failures[0].Message, StringComparison.Ordinal);
        Assert.Null(failures[0].Exception);
        Assert.Contains("connection refused or reset", failures[1].Message, StringComparison.Ordinal);
        Assert.Null(failures[1].Exception);
        Assert.Contains("InvalidOperationException", failures[2].Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(failures[2].Exception);
    }

    [Fact]
    public async Task One_hundred_retries_of_the_same_fault_log_one_Error_and_a_reminder_every_ten_minutes()
    {
        var f = new Fixture(restartDelay: TimeSpan.FromSeconds(15), healthTimeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromSeconds(1));
        const int attempts = 100;
        for (var i = 0; i < attempts; i++)
        {
            f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
            f.Client.EnqueueVersionFailure(Unauthorized());
        }

        await f.Service.StartAsync(CancellationToken.None); // attempt 1
        for (var attempt = 2; attempt <= attempts; attempt++)
        {
            await f.RetryOnceAsync(); // attempts 2..100, 15 s apart: 24 minutes 45 seconds in all
        }

        Assert.Equal(attempts, f.Launcher.StartCalls.Count);
        var errors = f.Log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        var reminders = f.Log.Entries.Where(e => e.Message.Contains("still failing", StringComparison.Ordinal)).ToList();

        Assert.Single(errors);
        Assert.Contains("HTTP 401", errors[0].Message, StringComparison.Ordinal);
        Assert.Equal(2, reminders.Count); // at 10 and 20 minutes (attempts 41 and 81)
        Assert.Contains("(41 attempts since", reminders[0].Message, StringComparison.Ordinal);
        Assert.Contains("(81 attempts since", reminders[1].Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", reminders[0].Message, StringComparison.Ordinal);
        Assert.All(f.Log.Entries.Where(e => e.Level >= LogLevel.Warning), e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task When_the_fault_changes_between_attempts_the_new_cause_is_logged_in_full_at_once()
    {
        var f = new Fixture(restartDelay: TimeSpan.FromSeconds(15), healthTimeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++)
        {
            f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        }

        f.Client.EnqueueVersionFailure(Unauthorized());
        f.Client.EnqueueVersionFailure(Unauthorized());
        f.Client.EnqueueVersionFailure(new HttpRequestException("actively refused"));

        await f.Service.StartAsync(CancellationToken.None);
        await f.RetryOnceAsync();
        await f.RetryOnceAsync();

        var errors = f.Log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains("HTTP 401", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("connection refused or reset", errors[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_after_repeated_failures_is_logged_once_at_Information_with_the_failed_attempt_count()
    {
        var f = new Fixture(restartDelay: TimeSpan.FromSeconds(15), healthTimeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromSeconds(1));
        for (var i = 0; i < 8; i++)
        {
            f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        }

        for (var i = 0; i < 7; i++)
        {
            f.Client.EnqueueVersionFailure(Unauthorized());
        }

        // The eighth attempt's core/version succeeds (empty queue).
        await f.Service.StartAsync(CancellationToken.None);
        for (var attempt = 2; attempt <= 8; attempt++)
        {
            await f.RetryOnceAsync();
        }

        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        var recovered = Assert.Single(f.Log.Entries, e => e.Message.Contains("working again", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Contains("after 7 failed attempts", recovered.Message, StringComparison.Ordinal);
        Assert.Single(f.Log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_port_held_by_another_process_for_a_long_time_logs_one_Warning_and_a_reminder_every_ten_minutes()
    {
        var f = new Fixture(restartDelay: TimeSpan.FromSeconds(15));
        const string held = "Port 5572 is held by PID 4242 (C:\\other\\thing.exe), which is not a Bosun-launched rclone rcd.";
        for (var i = 0; i < 100; i++)
        {
            f.Guard.Enqueue(new RcPortCheck(RcPortCheckOutcome.HeldByOtherProcess, held));
        }

        await f.Service.StartAsync(CancellationToken.None);
        for (var attempt = 2; attempt <= 100; attempt++)
        {
            await f.RetryOnceAsync();
        }

        Assert.Equal(100, f.Guard.EnsureFreeCalls);
        var warnings = f.Log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.Equal(3, warnings.Count); // the message, then reminders at attempts 41 and 81
        Assert.Equal(held, warnings[0].Message);
        Assert.Contains("still failing", warnings[1].Message, StringComparison.Ordinal);
    }

    private static async Task AdvanceUntilCompleteAsync(FakeTimeProvider time, Task task, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            time.Advance(step);
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private sealed class Fixture
    {
        private readonly TimeSpan restartDelay;

        public Fixture(TimeSpan? restartDelay = null, TimeSpan? healthTimeout = null, TimeSpan? pollInterval = null)
        {
            this.restartDelay = restartDelay ?? TimeSpan.FromSeconds(5);
            var options = new RcloneProcessServiceOptions
            {
                RcloneRcPort = 5572,
                RcloneConfigPath = @"C:\fixture\rclone.conf",
                RestartDelay = this.restartDelay,
                HealthCheckTimeout = healthTimeout ?? TimeSpan.FromSeconds(15),
                HealthCheckPollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250),
            };

            Service = new RcloneProcessService(
                Launcher, Client, options, Time, Log, new RcloneRcCredential("bosun-test-user", "bosun-test-pass"), Guard);
        }

        public FakeRcloneProcessLauncher Launcher { get; } = new();
        public FakeRcloneClient Client { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeRcPortGuard Guard { get; } = new();
        public LevelRecordingLogger<RcloneProcessService> Log { get; } = new();
        public RcloneProcessService Service { get; }

        /// <summary>Lets the supervise loop wait out one RestartDelay and run the next attempt. Waits
        /// for the delay timer to exist and for the attempt to finish, so none of it is a bet on
        /// thread-pool timing.</summary>
        public async Task RetryOnceAsync()
        {
            await Time.WhenActiveTimerCountAtLeastAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            var guardCalls = Guard.EnsureFreeCalls;
            Time.Advance(restartDelay);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Guard.EnsureFreeCalls == guardCalls && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1).ConfigureAwait(false);
            }

            // The attempt is over once it has gone healthy, or the loop is waiting out the next delay.
            while (DateTime.UtcNow < deadline)
            {
                if (Service.Status == RcloneProcessStatus.Healthy || Time.WhenActiveTimerCountAtLeastAsync(1).IsCompleted)
                {
                    return;
                }

                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }
}
