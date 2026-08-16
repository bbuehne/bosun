using System.Net.Http;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 4: unmount is confirmed against <c>mount/listmounts</c>, never
/// assumed from the unmount call's own outcome; a clean unmount that does not confirm within the
/// timeout escalates to a forced re-attempt, itself verified the same way; and a drain that STILL
/// cannot be confirmed never declares victory -- it keeps retrying rather than transitioning to
/// <c>Disabled</c> on unconfirmed information.
/// </summary>
public sealed class DrainAndForcedUnmountTests
{
    [Fact]
    public async Task Clean_unmount_that_confirms_immediately_completes_the_drain_without_escalating()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        // On-demand hosts rest in Ready after a drain, not Mounted (rule 6) and not Disabled
        // (rule 6 governs the resting state; the auto-re-enable after drain -- see class remarks
        // on MountSupervisor.CompleteDrainAsync -- carries it straight back through Probing).
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    /// <summary>
    /// bs-fix-e5 defect 3 (spec amendment -- see the delivery report): superseded reading of
    /// docs/ARCHITECTURE.md §4. The diagram lists "user unmount" as a
    /// <c>Mounted -&gt; Draining</c> trigger, the only edge out of <c>Draining</c> is
    /// <c>-&gt; Disabled</c>, and <c>Disabled</c>'s only documented exit is "enable" -- read as a
    /// whole, a user-requested unmount parks the host rather than cycling straight back through
    /// <c>Probing</c> to a fresh auto-mount. Rule 6 ("on-demand hosts rest in Ready... they do not
    /// auto-mount") governs a tier's resting state, not the override of an explicit command, and
    /// ADR-005 is about probe failure, not about undoing a deliberate instruction. This test used to
    /// assert the opposite (a blink-and-return); flagged to the maintainer as `bs-cql` to reverse if
    /// he disagrees with this reading.
    /// </summary>
    [Fact]
    public async Task Persistent_host_stays_unmounted_after_a_user_requested_unmount()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("prod"));

        Assert.NotEqual(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Single(harness.Rclone.MountCalls); // no auto-remount after the user's drain
        Assert.True(harness.Snapshot("prod").UserParked);

        // ... and an explicit user mount request un-parks it and mounts again.
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("prod"));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
        Assert.False(harness.Snapshot("prod").UserParked);
    }

    [Fact]
    public async Task Unmount_that_does_not_confirm_within_timeout_escalates_and_then_confirms()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var global = HostFixtures.Global();
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // Confirms only on the 4th UnmountAsync call for "Q:": three regular retries (t=0, t=5,
        // t=10) don't confirm, so the third attempt (at t=10, the 10s DrainConfirmTimeout) also
        // triggers the escalation's own extra call -- the 4th -- which does confirm.
        harness.Rclone.ConfirmUnmountAfterCall("Q:", callNumber: 4);

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5)); // t=5: 2nd attempt, still not confirmed
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(2, harness.Rclone.UnmountCallCountFor("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5)); // t=10: 3rd attempt + escalation's 4th -> confirms
        Assert.Equal(4, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Unmount_that_never_confirms_never_declares_the_drain_complete()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        harness.Rclone.ConfirmUnmountAfterCall("Q:", callNumber: int.MaxValue);

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        for (var i = 0; i < 6; i++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        }

        // Kept retrying the whole time -- never gave up and never silently declared victory.
        Assert.True(harness.Rclone.UnmountCallCountFor("Q:") >= 6);
    }

    [Fact]
    public async Task Listmounts_failure_during_drain_verification_is_treated_as_still_mounted()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        harness.Rclone.ConfirmUnmountAfterCall("Q:", callNumber: int.MaxValue);
        harness.Rclone.MakeListMountsThrowOnce(new HttpRequestException("rcd unreachable"));

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        // Could not confirm (listmounts threw) -> must not have completed the drain.
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
    }

    /// <summary>
    /// bs-fix-e5 defect 4: <c>suspend_unmount_timeout_seconds</c> must actually bound when a
    /// suspend drain escalates, not be rounded up to the next multiple of the fixed 5s
    /// drain-retry interval. Before the fix, escalation was only ever checked when a retry
    /// happened to fall due, so any budget in <c>[5, 10)</c> seconds escalated at the same t=10 as
    /// the ordinary (non-suspend) 10s timeout -- the setting had no observable effect across most
    /// of its plausible range. Two budgets that straddle no shared 5s grid point (6s and 9s) must
    /// now escalate at measurably different times.
    /// </summary>
    [Fact]
    public async Task Suspend_budget_bounds_the_escalation_deadline_instead_of_rounding_up_to_the_retry_grid()
    {
        var sixSecondBudget = new SupervisorHarness(
            HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: 6), HostFixtures.Persistent("prod", drive: "P:")));
        await sixSecondBudget.StartAsync();
        sixSecondBudget.Rclone.ConfirmUnmountAfterCall("P:", callNumber: 4);
        await sixSecondBudget.RunAsync(() => sixSecondBudget.Supervisor.SuspendAsync());

        await sixSecondBudget.AdvanceAsync(TimeSpan.FromSeconds(5)); // t=5: short of the 6s budget
        Assert.Equal(MountState.Draining, sixSecondBudget.Snapshot("prod").State);
        Assert.Equal(2, sixSecondBudget.Rclone.UnmountCallCountFor("P:"));

        await sixSecondBudget.AdvanceAsync(TimeSpan.FromSeconds(1)); // t=6: budget reached -> escalate and confirm
        Assert.Equal(4, sixSecondBudget.Rclone.UnmountCallCountFor("P:"));
        Assert.NotEqual(MountState.Draining, sixSecondBudget.Snapshot("prod").State);

        var nineSecondBudget = new SupervisorHarness(
            HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: 9), HostFixtures.Persistent("prod", drive: "P:")));
        await nineSecondBudget.StartAsync();
        nineSecondBudget.Rclone.ConfirmUnmountAfterCall("P:", callNumber: 4);
        await nineSecondBudget.RunAsync(() => nineSecondBudget.Supervisor.SuspendAsync());

        // At t=6 the 6s-budget host above was already done; this 9s-budget host must not be --
        // proof the two settings are no longer indistinguishable.
        await nineSecondBudget.AdvanceAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(MountState.Draining, nineSecondBudget.Snapshot("prod").State);

        await nineSecondBudget.AdvanceAsync(TimeSpan.FromSeconds(3)); // t=9: now it escalates and confirms
        Assert.Equal(4, nineSecondBudget.Rclone.UnmountCallCountFor("P:"));
        Assert.NotEqual(MountState.Draining, nineSecondBudget.Snapshot("prod").State);
    }
}
