using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// Invariant I8, docs/ARCHITECTURE.md §4 rule 5 and the Backoff section, and the manual protocol in
/// docs/OPERATIONS.md T1/T2 -- T2 being, per CLAUDE.md §7, "the acceptance test that matters".
/// </summary>
public sealed class SuspendResumeAcceptanceTests
{
    /// <summary>
    /// docs/OPERATIONS.md T2, automated end to end: a LAN host is mounted, the lid closes, the
    /// machine wakes somewhere the host is unreachable, and later returns to the LAN.
    /// </summary>
    /// <remarks>
    /// Three separate ways to fail this, each a real bug: remounting on resume without re-probing
    /// (a wedged drive letter on the new network); leaving the host parked so that returning to the
    /// LAN needs manual intervention; and waiting out the backoff ladder after the network address
    /// changes instead of probing immediately.
    /// </remarks>
    [Fact]
    public async Task T2_lid_closed_on_one_network_and_opened_on_another_leaves_no_mount_and_recovers_on_return()
    {
        var lan = HostFixtures.Persistent("lan-nas", probeIntervalSeconds: 0, drive: "L:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), lan));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("lan-nas"));

        // Lid closes.
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.Equal(MountState.Disabled, harness.State("lan-nas"));
        Assert.Equal(1, harness.Rclone.UnmountCount("L:"));

        // Opened somewhere else: the LAN host does not answer.
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(MountState.Unreachable, harness.State("lan-nas"));
        Assert.Equal(1, harness.Rclone.TotalMountCalls); // no remount attempt at all
        Assert.Equal(1, harness.Probe.DeepCount("lan-nas")); // exactly the one from the original mount

        // Time passes on the foreign network; the ladder retries and keeps failing, and at no point
        // does a drive letter come back.
        for (var i = 0; i < 10; i++)
        {
            await harness.AdvanceSecondsAsync(300);
            Assert.Equal(MountState.Unreachable, harness.State("lan-nas"));
        }

        Assert.Equal(1, harness.Rclone.TotalMountCalls);

        // Back on the LAN. The address change must not wait out the 300s rung.
        harness.Probe.DefaultShallow = ShallowProbeOutcome.Success;
        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(MountState.Mounted, harness.State("lan-nas"));
        Assert.Equal(2, harness.Rclone.MountCount("L:"));
        Assert.Equal(2, harness.Probe.DeepCount("lan-nas"));
    }

    /// <summary>
    /// Bug this catches: a suspend handler that stops at the first host, or that only handles the
    /// persistent tier. Any host left mounted through a suspend is the T2 failure mode.
    /// </summary>
    [Fact]
    public async Task Suspend_drains_every_mounted_host_not_only_the_first()
    {
        var a = HostFixtures.Persistent("alpha", drive: "A:");
        var b = HostFixtures.Persistent("bravo", drive: "B:");
        var c = HostFixtures.OnDemand("charlie", drive: "C:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), a, b, c));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("charlie"));
        Assert.Equal(MountState.Mounted, harness.State("alpha"));
        Assert.Equal(MountState.Mounted, harness.State("bravo"));
        Assert.Equal(MountState.Mounted, harness.State("charlie"));

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        Assert.Equal(1, harness.Rclone.UnmountCount("A:"));
        Assert.Equal(1, harness.Rclone.UnmountCount("B:"));
        Assert.Equal(1, harness.Rclone.UnmountCount("C:"));
        Assert.Equal(MountState.Disabled, harness.State("alpha"));
        Assert.Equal(MountState.Disabled, harness.State("bravo"));
        Assert.Equal(MountState.Disabled, harness.State("charlie"));
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §3: "the suspend handler must complete quickly. Windows does not wait
    /// indefinitely." Bug this catches: a suspend that blocks until every unmount confirms, so one
    /// wedged mount stops the other hosts being told to unmount at all before the machine sleeps.
    /// </summary>
    [Fact]
    public async Task Suspend_issues_every_unmount_even_when_none_of_them_confirm()
    {
        var a = HostFixtures.Persistent("alpha", drive: "A:");
        var b = HostFixtures.Persistent("bravo", drive: "B:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), a, b));

        await harness.StartAsync();
        harness.Rclone.ConfirmUnmountAtCall("A:", int.MaxValue);
        harness.Rclone.ConfirmUnmountAtCall("B:", int.MaxValue);

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        Assert.Equal(1, harness.Rclone.UnmountCount("A:"));
        Assert.Equal(1, harness.Rclone.UnmountCount("B:"));
        Assert.Equal(MountState.Draining, harness.State("alpha"));
        Assert.Equal(MountState.Draining, harness.State("bravo"));
    }

    /// <summary>
    /// Bug this catches: the post-drain auto re-enable firing while the machine is suspended, so a
    /// drive letter is recreated in the seconds between the suspend handler running and the box
    /// actually sleeping -- exactly the mount Invariant I8 just removed.
    /// </summary>
    [Fact]
    public async Task A_suspended_host_does_not_bring_itself_back_up()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        var probesAtSuspend = harness.Probe.TotalShallowCalls;

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(MountState.Disabled, harness.State("prod"));
        Assert.Equal(probesAtSuspend, harness.Probe.TotalShallowCalls);
        Assert.Equal(1, harness.Rclone.TotalMountCalls);
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §4 rule 5: "on resume, everything previously enabled goes to
    /// <c>Probing</c>" -- and the Backoff section's "reset to zero by ... a power resume". Bug this
    /// catches: a resume that handles one state (say, hosts parked by suspend) and silently skips
    /// hosts sitting at a deep backoff rung, which would leave them waiting out five minutes after
    /// a wake -- and a resume that reawakens a <c>mount.mode = "none"</c> host it has no business
    /// touching.
    /// </summary>
    [Fact]
    public async Task Resume_re_probes_every_enabled_host_immediately_and_leaves_disabled_hosts_alone()
    {
        var mounted = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var stranded = HostFixtures.OnDemand("archive", drive: "Q:");
        var jump = HostFixtures.None("jump");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), mounted, stranded, jump));
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("archive"), ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(MountState.Unreachable, harness.State("archive"));

        // Walk "archive" out to the last rung of the default [5, 15, 30, 60, 300] ladder.
        foreach (var rung in new[] { 5, 15, 30, 60 })
        {
            await harness.AdvanceSecondsAsync(rung);
        }

        Assert.Equal(5, harness.Snapshot("archive").ConsecutiveIdleFailures);

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("archive"), ShallowProbeOutcome.Success);

        var prodProbes = harness.ShallowProbesFor("prod");
        var archiveProbes = harness.ShallowProbesFor("archive");

        // No clock movement at all: resume must probe now, not re-arm a 300s timer.
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(prodProbes + 1, harness.ShallowProbesFor("prod"));
        Assert.Equal(archiveProbes + 1, harness.ShallowProbesFor("archive"));
        Assert.Equal(0, harness.ShallowProbesFor("jump"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(MountState.Disabled, harness.State("jump"));
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §4 Backoff: "reset to zero by a network address change". The reset has
    /// to happen <b>before</b> the forced probe, otherwise the failure that probe records lands on
    /// the old rung. Bug this catches: resetting after the probe, or not at all -- which makes
    /// docking and undocking feel sluggish (up to five minutes) instead of immediate, the exact
    /// property the Backoff section says the reset exists for.
    /// </summary>
    [Fact]
    public async Task A_network_change_restarts_the_backoff_ladder_at_its_first_rung()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;

        await harness.StartAsync();
        await harness.AdvanceSecondsAsync(5);
        await harness.AdvanceSecondsAsync(15);
        await harness.AdvanceSecondsAsync(30); // four consecutive failures -> next rung is 60s

        Assert.Equal(4, harness.Snapshot("archive").ConsecutiveIdleFailures);

        // The address changes; the host is still unreachable, so this probe fails too -- but the
        // ladder must have restarted, so the NEXT retry is due in 5s rather than 60s.
        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());
        Assert.Equal(1, harness.Snapshot("archive").ConsecutiveIdleFailures);

        var probes = harness.ShallowProbesFor("archive");
        await harness.AdvanceSecondsAsync(4);
        Assert.Equal(probes, harness.ShallowProbesFor("archive"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(probes + 1, harness.ShallowProbesFor("archive"));
    }

    /// <summary>
    /// The ordering docs/OPERATIONS.md T1 produces in practice: the machine wakes before a slow
    /// suspend-time unmount has been confirmed. Bug this catches: a host stranded in
    /// <c>Draining</c> forever because the resume path skipped it and nothing ever re-enabled it
    /// once the drain finally confirmed -- a drive that never comes back until the app is
    /// restarted.
    /// </summary>
    [Fact]
    public async Task A_host_still_draining_when_the_machine_resumes_is_recovered_once_the_drain_confirms()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        harness.Rclone.ConfirmUnmountAtCall("P:", 3);

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.Equal(MountState.Draining, harness.State("prod"));

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());
        Assert.Equal(MountState.Draining, harness.State("prod"));

        await harness.AdvanceSecondsAsync(5);
        await harness.AdvanceSecondsAsync(5);

        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(2, harness.Rclone.MountCount("P:"));
    }
}
