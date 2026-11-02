using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-u4a item 4 / docs/ARCHITECTURE.md §4 rule 5: a host that is already <c>Draining</c> for an
/// ordinary reason when suspend arrives escalates to a forced unmount on the SUSPEND budget, not on
/// the ordinary 10 s <c>DrainConfirmTimeout</c> -- but only ever sooner, never later. Every test
/// uses the fake rclone client and fake clock; nothing touches a real mount.
/// </summary>
public sealed class SuspendWhileDrainingTests
{
    private const int SuspendBudgetSeconds = 3;

    private static async Task<SupervisorHarness> MountedOnDemandAsync(string key, string drive)
    {
        var harness = new SupervisorHarness(
            HostFixtures.Build(
                HostFixtures.Global(suspendUnmountTimeoutSeconds: SuspendBudgetSeconds),
                HostFixtures.OnDemand(key, drive: drive)));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync(key));
        Assert.Equal(MountState.Mounted, harness.Snapshot(key).State);
        return harness;
    }

    [Fact]
    public async Task Host_already_draining_when_suspend_arrives_escalates_at_suspend_arrival_plus_budget_not_at_ten_seconds()
    {
        var harness = await MountedOnDemandAsync("archive", "Q:");

        // Calls for Q:: #1 the user drain at t=0, #2 the immediate re-attempt when suspend arrives,
        // #3 the check at the new deadline, #4 the escalation's own re-issue -- which confirms.
        harness.Rclone.ConfirmUnmountAfterCall("Q:", callNumber: 4);
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1)); // t=1: before the 5 s retry tick
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync()); // deadline = t=1 + 3 = t=4

        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(2, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromMilliseconds(2900)); // t=3.9: short of t=4
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(2, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromMilliseconds(100)); // t=4: suspend deadline, well before t=10

        Assert.Equal(4, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Host_draining_after_a_user_unmount_stays_parked_when_suspend_shortens_its_deadline()
    {
        var harness = await MountedOnDemandAsync("archive", "Q:");
        harness.Rclone.ConfirmUnmountAfterCall("Q:", callNumber: 4);
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));

        // The drain cause is untouched: it completes as the user's unmount did, and because the
        // system is suspended it is not re-enabled (ResumeAsync's job).
        var snapshot = harness.Snapshot("archive");
        Assert.Equal(MountState.Disabled, snapshot.State);
        Assert.True(snapshot.UserParked);
    }

    [Fact]
    public async Task Suspend_never_lengthens_the_deadline_of_a_drain_that_is_already_closer_to_escalating()
    {
        var harness = await MountedOnDemandAsync("archive", "Q:");

        // Calls: #1 t=0, #2 t=5 retry, then at t=10 the retry (#3) plus the escalation re-issue (#4).
        harness.Rclone.ConfirmUnmountAfterCall("Q:", callNumber: 4);
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(4)); // t=9: one second left of the ordinary 10 s
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync()); // suspend deadline would be t=12

        // Nothing to shorten, so no extra attempt is made by the suspend itself.
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(2, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1)); // t=10: still the ordinary deadline

        Assert.Equal(4, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task A_second_suspend_does_not_push_back_the_deadline_of_a_drain_the_first_suspend_began()
    {
        var harness = new SupervisorHarness(
            HostFixtures.Build(
                HostFixtures.Global(suspendUnmountTimeoutSeconds: SuspendBudgetSeconds),
                HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        // Calls: #1 the suspend drain at t=0; at t=3 the retry (#2) plus the escalation re-issue (#3).
        harness.Rclone.ConfirmUnmountAfterCall("P:", callNumber: 3);
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync()); // would be t=5 if it re-based

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("P:"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1)); // t=3: the first suspend's deadline

        Assert.Equal(3, harness.Rclone.UnmountCallCountFor("P:"));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("prod").State);
    }

    [Fact]
    public async Task Host_that_drained_before_suspend_is_untouched_by_the_suspend_sweep()
    {
        var harness = await MountedOnDemandAsync("archive", "Q:");
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        var callsBefore = harness.Rclone.UnmountCallCountFor("Q:");

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        Assert.Equal(callsBefore, harness.Rclone.UnmountCallCountFor("Q:"));
    }
}
