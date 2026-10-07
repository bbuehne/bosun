using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-qcs, part 3: the supervisor's own timers keep calling rclone while rclone is down -- the
/// reconciliation tick every 30 s, a stuck drain every 5 s -- and each used to log a Warning with a
/// stack trace every time. They now log once, then a reminder every ten minutes.
/// </summary>
public sealed class RcOutageLogVolumeTests
{
    private static RcloneRcException Unauthorized() => new("mount/listmounts", 401, "unauthorized");

    [Fact]
    public async Task A_listmounts_failure_that_lasts_through_100_reconciliation_ticks_logs_once_then_a_reminder_per_ten_minutes()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Rclone.MakeListMountsThrow(Unauthorized());

        for (var tick = 0; tick < 100; tick++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // 50 minutes in all
        }

        var warnings = harness.Log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        var first = Assert.Single(warnings, e => e.Message.Contains("Reconciliation: mount/listmounts failed", StringComparison.Ordinal));
        Assert.Null(first.Exception); // a 401 is explained by its text
        Assert.Contains("HTTP 401", first.Message, StringComparison.Ordinal);

        var reminders = warnings.Where(e => e.Message.Contains("still failing", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, reminders.Count); // at 10, 20, 30 and 40 minutes
        Assert.Equal(1 + reminders.Count, warnings.Count);
        Assert.Contains("Reconciliation: mount/listmounts: still failing: HTTP 401", reminders[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listmounts_working_again_is_logged_once_and_a_later_failure_is_a_new_first_occurrence()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Rclone.MakeListMountsThrow(Unauthorized());
        for (var tick = 0; tick < 5; tick++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        }

        harness.Rclone.StopThrowingFromListMounts();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        harness.Rclone.MakeListMountsThrow(Unauthorized());
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        var recovered = Assert.Single(harness.Log.Entries, e => e.Message.Contains("working again", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Contains("after 5 failed ticks", recovered.Message, StringComparison.Ordinal);
        Assert.Equal(
            2, harness.Log.Entries.Count(e => e.Message.Contains("Reconciliation: mount/listmounts failed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_drain_that_cannot_reach_rclone_for_an_hour_logs_each_failing_call_once_then_reminders()
    {
        var archive = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), archive));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // Both calls the drain makes fail, as they do when rcd is gone: every 5 s, forever.
        harness.Rclone.MakeUnmountThrow(Unauthorized());
        harness.Rclone.MakeListMountsThrow(Unauthorized());
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        for (var step = 0; step < 720; step++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(5)); // one hour
        }

        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.True(harness.Rclone.UnmountCallCountFor("Q:") > 700, "the drain must keep retrying");

        var warnings = harness.Log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        // Per failing call: the first message, plus reminders at 10-minute intervals. Not 1,400 lines.
        Assert.Single(warnings, e => e.Message.Contains("mount/unmount call failed", StringComparison.Ordinal));
        Assert.Single(warnings, e => e.Message.Contains("mount/listmounts failed while verifying", StringComparison.Ordinal));
        Assert.InRange(warnings.Count, 3, 40);

        // The one-off escalation notice ("Forced unmount re-attempt failed") is logged once per drain and
        // is not what this test is about; every repeating line must be free of stack traces.
        Assert.All(
            warnings.Where(w => !w.Message.Contains("Forced unmount re-attempt", StringComparison.Ordinal)),
            w => Assert.Null(w.Exception));
    }
}
