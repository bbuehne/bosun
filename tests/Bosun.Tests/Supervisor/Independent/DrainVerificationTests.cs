using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 4: "<c>Draining</c> calls <c>mount/unmount</c>. If unmount does not
/// confirm within the timeout, escalate to a forced unmount, then verify against
/// <c>mount/listmounts</c>. Never leave the state machine believing a drive is gone when it is
/// not."
/// </summary>
/// <remarks>
/// The dangerous direction is asymmetric. Believing a drive is still up when it is gone costs one
/// wasted unmount call. Believing it is gone when it is still up means the supervisor will happily
/// mount something else on that letter, or report the host as clean while Explorer is still hanging
/// on it. Every test here pushes on that direction.
/// </remarks>
public sealed class DrainVerificationTests
{
    /// <summary>
    /// Bug this catches: treating a <c>mount/unmount</c> call that returned without an error as
    /// proof the drive is gone. rclone answering the RPC is not the same as WinFsp having released
    /// the letter.
    /// </summary>
    [Fact]
    public async Task An_unmount_call_that_returns_successfully_is_not_by_itself_treated_as_confirmation()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // The call never throws, but the mount only actually disappears on the 4th attempt.
        harness.Rclone.ConfirmUnmountAtCall("Q:", 4);

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        Assert.Equal(MountState.Draining, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
    }

    /// <summary>
    /// Bug this catches: an escalation that declares victory on the forced unmount's own outcome
    /// instead of re-asking <c>mount/listmounts</c>. Asserted structurally -- every unmount in the
    /// log is followed by a listmounts before anything else happens -- so it holds for the escalated
    /// attempt as well as the ordinary ones.
    /// </summary>
    [Fact]
    public async Task Every_unmount_attempt_including_the_escalated_one_is_verified_against_listmounts()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        harness.Rclone.ConfirmUnmountAtCall("Q:", 4);

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        await harness.AdvanceSecondsAsync(5);
        await harness.AdvanceSecondsAsync(5); // t=10: the 10s confirm timeout -> escalation

        var drainCalls = harness.Log.Filtered("unmount|", "listmounts|");
        for (var i = 0; i < drainCalls.Count; i++)
        {
            if (!drainCalls[i].StartsWith("unmount|", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(i + 1 < drainCalls.Count, $"unmount #{i} was never verified against listmounts");
            Assert.StartsWith("listmounts|", drainCalls[i + 1], StringComparison.Ordinal);
        }

        Assert.Equal(4, harness.Rclone.UnmountCount("Q:"));
        Assert.NotEqual(MountState.Draining, harness.State("archive"));
    }

    /// <summary>
    /// The case the existing suite only exercises for a single blip: <c>mount/listmounts</c> that
    /// keeps failing, i.e. an <c>rclone rcd</c> that is down rather than briefly unlucky. Bug this
    /// catches: treating an unanswerable listmounts as "no mounts, therefore gone", which converts
    /// an rcd outage into a state machine that reports every drive as cleanly unmounted while the
    /// letters are all still up.
    /// </summary>
    [Fact]
    public async Task A_drain_never_completes_while_listmounts_cannot_be_reached()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        harness.Rclone.ListMountsAlwaysFails = true;
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        for (var i = 0; i < 12; i++)
        {
            await harness.AdvanceSecondsAsync(5);
            Assert.Equal(MountState.Draining, harness.State("archive"));
        }

        // ... and once rcd answers again, the drain concludes on real information.
        harness.Rclone.ListMountsAlwaysFails = false;
        await harness.AdvanceSecondsAsync(5);

        Assert.NotEqual(MountState.Draining, harness.State("archive"));
    }

    /// <summary>
    /// Bug this catches: remounting a drive letter whose previous unmount was never confirmed --
    /// two rclone mounts on one letter, which is the worst available outcome for Explorer.
    /// </summary>
    [Fact]
    public async Task A_persistent_host_is_never_remounted_while_its_unmount_is_unconfirmed()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 1, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        Assert.Equal(1, harness.Rclone.MountCount("P:"));

        harness.Rclone.ConfirmUnmountAtCall("P:", int.MaxValue);
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("prod"), ShallowProbeOutcome.Timeout, times: 1);

        await harness.AdvanceSecondsAsync(60); // one failure at threshold 1 -> Draining

        for (var i = 0; i < 120; i++)
        {
            await harness.AdvanceSecondsAsync(5); // ten minutes of unconfirmed drain
            Assert.Equal(MountState.Draining, harness.State("prod"));
        }

        Assert.Equal(1, harness.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// Invariant I8 / docs/ARCHITECTURE.md §3: "the suspend handler must complete quickly ... issue
    /// unmounts with a short timeout". A suspend drain must escalate on
    /// <c>global.suspend_unmount_timeout_seconds</c>, not on the ordinary drain timeout. Bug this
    /// catches: ignoring the suspend budget, so the forced unmount is attempted long after Windows
    /// has stopped waiting.
    /// </summary>
    [Fact]
    public async Task A_suspend_drain_escalates_on_the_suspend_budget_not_the_ordinary_drain_timeout()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(suspendUnmountTimeoutSeconds: 3);

        var suspending = new IndependentHarness(HostFixtures.Build(global, host));
        await suspending.StartAsync();
        suspending.Rclone.ConfirmUnmountAtCall("P:", 3);
        await suspending.RunAsync(() => suspending.Supervisor.SuspendAsync());
        await suspending.AdvanceSecondsAsync(5);

        // t=0 attempt, t=5 attempt, and -- because 5s is past the 3s suspend budget -- the
        // escalated forced attempt in the same step, which is the one that lands.
        Assert.Equal(3, suspending.Rclone.UnmountCount("P:"));

        var ordinary = new IndependentHarness(HostFixtures.Build(global, host));
        await ordinary.StartAsync();
        ordinary.Rclone.ConfirmUnmountAtCall("P:", 3);
        await ordinary.RunAsync(() => ordinary.Supervisor.RequestUnmountAsync("prod"));
        await ordinary.AdvanceSecondsAsync(5);

        // Same clock, same script, no suspend: 5s is inside the ordinary confirm timeout, so no
        // escalation yet. If the suspend budget were being ignored, both counts would be 2.
        Assert.Equal(2, ordinary.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Draining, ordinary.State("prod"));
    }
}
