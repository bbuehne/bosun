using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// ADR-023 decision 1 and docs/ARCHITECTURE.md §4 rule 5 ("No mount happens while the supervisor is
/// suspended -- or while a suspend is pending"): every route into <c>mount/mount</c> is closed from the
/// instant <c>SuspendAsync</c> is CALLED, including work that was queued AHEAD of the suspend and runs
/// before the suspend is dequeued. Written from the ADR, not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of every test.</b> A command that is NOT preemptible (a "retry now" whose probe the test
/// holds, ADR-023 decision 4) occupies the channel. Behind it the test queues one route to
/// <c>mount/mount</c>, THEN calls <c>SuspendAsync</c>, then releases the blocker. The route therefore runs
/// while the suspend is pending but not yet dequeued -- the window in which the old <c>suspended</c> flag
/// was still false. A drive created there survives the sleep, which is the I8 failure.
/// </para>
/// <para>
/// Each test then resumes and checks the host comes back, so a "gate" that simply never reopens cannot
/// pass.
/// </para>
/// </remarks>
public sealed class MountGateWhilePendingSpecTests
{
    private const string Blocker = "slow";

    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3),
            [HostFixtures.Persistent(Blocker, probeIntervalSeconds: 60, drive: "S:"), .. hosts]));

    /// <summary>Starts the harness with the blocker host unreachable (so it never mounts and never
    /// interferes) and every other host in its natural resting state.</summary>
    private static async Task<PreemptSpecHarness> StartedAsync(params HostConfig[] hosts)
    {
        var h = NewHarness(hosts);
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf(Blocker), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State(Blocker));
        return h;
    }

    /// <summary>
    /// Occupies the channel with a "retry now" on the blocker whose probe is held, runs
    /// <paramref name="queueRoute"/> (which must enqueue its work behind it), calls <c>SuspendAsync</c>, then
    /// releases the blocker and lets everything finish. Returns the route's task, which may be faulted.
    /// </summary>
    private static async Task<Task> RouteQueuedAheadOfSuspendAsync(PreemptSpecHarness h, Func<Task> queueRoute)
    {
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf(Blocker), OnCancel.Ignore);
        var blocker = h.Supervisor.RequestRetryNowAsync(Blocker);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the blocker's retry-now probe");

        var route = queueRoute();
        var suspend = SuspendCaller.Call(h.Supervisor);
        hold.Release(ShallowProbeOutcome.ConnectionRefused);

        await h.FinishAsync(pump, "the blocker never finished", blocker, route, suspend);
        await suspend;
        Assert.False(hold.TokenCancelled, "a command's probe was cancelled by the suspend (ADR-023 decision 4: commands are not preempted)");
        return route;
    }

    private static void AssertNeverEnteredMounting(PreemptSpecHarness h, string hostKey) =>
        Assert.DoesNotContain(h.Transitions(hostKey), t => t.To == MountState.Mounting);

    /// <summary>
    /// <b>Protects:</b> ADR-023 consequence 3: "A mount/mount request queued ahead of a suspend is now refused
    /// with 'the system is suspended'".
    /// <b>Catches:</b> <c>RequestMountAsync</c> still gated on the dequeue-time <c>suspended</c> flag: the
    /// user's click mounts, and the drive is (at best) drained a moment later or (at worst) survives.
    /// </summary>
    [Fact]
    public async Task A_users_mount_click_queued_ahead_of_the_suspend_is_refused()
    {
        var h = await StartedAsync(HostFixtures.OnDemand("od", drive: "Q:"));
        Assert.Equal(MountState.Ready, h.State("od"));

        var route = await RouteQueuedAheadOfSuspendAsync(h, () => h.Supervisor.RequestMountAsync("od"));

        var refused = await Assert.ThrowsAsync<MountRequestRefusedException>(() => route);
        Assert.Contains("suspended", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, h.MountCalls("Q:"));
        AssertNeverEnteredMounting(h, "od");
    }

    /// <summary>
    /// <b>Protects:</b> decision 1 (<c>TryBeginMountAsync</c> reads the combined gate) for the route "retry
    /// now" -> <c>Ready</c> -> persistent auto-mount; rule 5's "A host held back this way simply stays Ready";
    /// and decision 6: the retry's probe is a command and still runs.
    /// <b>Catches:</b> the auto-mount on arrival at <c>Ready</c> not seeing a pending suspend.
    /// </summary>
    [Fact]
    public async Task A_retry_now_on_an_unreachable_host_queued_ahead_of_the_suspend_probes_but_does_not_mount()
    {
        var h = NewHarness(HostFixtures.Persistent("pz", probeIntervalSeconds: 60, drive: "P:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf(Blocker), ShallowProbeOutcome.ConnectionRefused);
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.ConnectionRefused, times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("pz"));
        var probesBefore = h.ShallowCalls("pz");

        await RouteQueuedAheadOfSuspendAsync(h, () => h.Supervisor.RequestRetryNowAsync("pz"));

        Assert.True(h.ShallowCalls("pz") > probesBefore, "retry now is a command and must still probe (ADR-023 decision 6)");
        Assert.Equal(MountState.Ready, h.State("pz"));
        Assert.Equal(0, h.MountCalls("P:"));
        AssertNeverEnteredMounting(h, "pz");

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("pz"));
        Assert.Equal(1, h.MountCalls("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 2's last sentence for the idle probe ("A tick that starts while a suspend is
    /// pending ... skips its I/O") and decision 1 for the auto-mount it would otherwise trigger.
    /// <b>Catches:</b> a backoff-ladder tick queued just before the suspend probing, finding the host back,
    /// and auto-mounting it in the seconds before sleep.
    /// </summary>
    [Fact]
    public async Task An_idle_probe_tick_queued_ahead_of_the_suspend_neither_probes_nor_mounts()
    {
        var h = NewHarness(HostFixtures.Persistent("pz", probeIntervalSeconds: 60, drive: "P:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf(Blocker), ShallowProbeOutcome.ConnectionRefused);
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.ConnectionRefused, times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("pz"));
        var probesBefore = h.ShallowCalls("pz");

        await RouteQueuedAheadOfSuspendAsync(h, () =>
        {
            h.FireTimers(TimeSpan.FromSeconds(6)); // pz's first backoff rung (5 s) fires and queues its tick
            return Task.CompletedTask;
        });

        Assert.Equal(probesBefore, h.ShallowCalls("pz"));
        Assert.Equal(0, h.MountCalls("P:"));
        AssertNeverEnteredMounting(h, "pz");

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("pz"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 1, "the mount-retry callback" and rule 5: "The mount-retry timer (a failed
    /// mount's paced re-enable) is also skipped while suspended, so it does not probe in those seconds
    /// either: the host stays Disabled, and resume re-enables it."
    /// <b>Catches:</b> the mount-retry callback gated only on the dequeue-time flag, so a retry that fires
    /// while the suspend waits re-enables, probes and mounts the host.
    /// </summary>
    [Fact]
    public async Task The_mount_retry_timer_firing_while_a_suspend_is_pending_does_not_re_enable_the_host()
    {
        var h = NewHarness(HostFixtures.Persistent("pz", probeIntervalSeconds: 60, drive: "P:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf(Blocker), ShallowProbeOutcome.ConnectionRefused);
        h.Rclone.FailMount("P:", new InvalidOperationException("drive letter in use"), times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Disabled, h.State("pz"));
        Assert.Equal(1, h.MountCalls("P:"));
        var probesBefore = h.ShallowCalls("pz");

        await RouteQueuedAheadOfSuspendAsync(h, () =>
        {
            h.FireTimers(TimeSpan.FromSeconds(6)); // the first mount-retry rung (5 s)
            return Task.CompletedTask;
        });

        Assert.Equal(MountState.Disabled, h.State("pz"));
        Assert.Equal(probesBefore, h.ShallowCalls("pz"));
        Assert.Equal(1, h.MountCalls("P:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("pz"));
        Assert.Equal(2, h.MountCalls("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 1 for a config change that makes an idle, reachable on-demand host
    /// persistent (bs-7ck: "on-demand to persistent mounts immediately if idle and reachable").
    /// <b>Catches:</b> the config-change path mounting while a suspend is pending.
    /// </summary>
    [Fact]
    public async Task A_config_change_to_persistent_queued_ahead_of_the_suspend_does_not_mount()
    {
        var od = HostFixtures.OnDemand("od", probeIntervalSeconds: 60, drive: "Q:");
        var h = await StartedAsync(od);
        Assert.Equal(MountState.Ready, h.State("od"));

        var next = HostFixtures.Build(
            h.Store.Current.Global,
            [.. h.Store.Current.Hosts.Values.Where(x => x.Key != "od"), od with { Mount = od.Mount with { Mode = MountMode.Persistent } }]);

        await RouteQueuedAheadOfSuspendAsync(h, () =>
        {
            h.Store.Publish(next);
            return h.Supervisor.ConfigChangedAsync(next);
        });

        Assert.Equal(0, h.MountCalls("Q:"));
        AssertNeverEnteredMounting(h, "od");

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("od"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 1 ("<c>EnableHostAsync</c> returns before <c>SetState(Probing)</c> while a
    /// suspend is pending; the host stays Disabled and resume enables it") through the route "host added
    /// to the config".
    /// <b>Catches:</b> an enable that probes and, for a persistent host, mounts in the window before sleep.
    /// </summary>
    [Fact]
    public async Task A_host_added_by_a_config_change_queued_ahead_of_the_suspend_stays_Disabled_until_resume()
    {
        var h = await StartedAsync();
        var added = HostFixtures.Persistent("nw", probeIntervalSeconds: 60, drive: "N:");
        var next = HostFixtures.Build(h.Store.Current.Global, [.. h.Store.Current.Hosts.Values, added]);

        await RouteQueuedAheadOfSuspendAsync(h, () =>
        {
            h.Store.Publish(next);
            return h.Supervisor.ConfigChangedAsync(next);
        });

        Assert.Equal(MountState.Disabled, h.State("nw"));
        Assert.Equal(0, h.ShallowCalls("nw"));
        Assert.DoesNotContain(h.Transitions("nw"), t => t.To == MountState.Probing);
        Assert.Equal(0, h.MountCalls("N:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("nw"));
    }

    /// <summary>
    /// <b>Protects:</b> rule 5 ("No mount happens while the supervisor is suspended") for the same route once
    /// the suspend has been dequeued: a host added while the machine is going to sleep.
    /// <b>Catches:</b> the enable path gated on the PENDING half only, letting a host added after the suspend
    /// ran go <c>Probing -> Ready -> Mounting</c> before the machine sleeps.
    /// </summary>
    /// <remarks>Deliberately silent on whether the host may reach <c>Probing</c> while suspended: ADR-023
    /// decision 1 says <c>EnableHostAsync</c> returns early "while a suspend is pending"; §4 rule 5 lists the
    /// same early return under "The same gate", which is <c>suspended || pending</c>. See the report.</remarks>
    [Fact]
    public async Task A_host_added_while_suspended_does_not_mount_until_resume()
    {
        var h = await StartedAsync();
        await h.SuspendAsync();
        var added = HostFixtures.Persistent("nw", probeIntervalSeconds: 60, drive: "N:");
        var next = HostFixtures.Build(h.Store.Current.Global, [.. h.Store.Current.Hosts.Values, added]);
        h.Store.Publish(next);

        await h.RunAsync(() => h.Supervisor.ConfigChangedAsync(next));
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(0, h.MountCalls("N:"));
        AssertNeverEnteredMounting(h, "nw");

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("nw"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 1 for <c>RepairAllAsync</c>, and §4 rule 5: "Unmount all &amp; re-probe" is
    /// refused with "the system is suspended".
    /// <b>Catches:</b> a repair that runs in the window: it drains with <c>DrainCause.Repair</c> (so the suspend
    /// no longer sees the host as one it drained) and its re-enable races the sleep.
    /// </summary>
    [Fact]
    public async Task A_repair_queued_ahead_of_the_suspend_is_refused_and_the_suspend_drains_instead()
    {
        var h = await StartedAsync(HostFixtures.Persistent("m1", probeIntervalSeconds: 60, drive: "M:"));
        Assert.Equal(MountState.Mounted, h.State("m1"));

        var route = await RouteQueuedAheadOfSuspendAsync(h, () => h.Supervisor.RepairAllAsync());

        await Assert.ThrowsAsync<RepairRefusedException>(() => route);
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("m1"));
        Assert.False(h.IsMountedInRclone("M:"));
        Assert.Equal(1, h.MountCalls("M:"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 1 for the ADR-022 sweep (rule 9a: "or the system is suspended again (rule 5),
    /// the remount is not attempted and the record is kept") when the gate-clearing event is queued AHEAD of
    /// the suspend.
    /// <b>Catches:</b> the sweep reading only the dequeue-time flag and remounting the user's drive seconds
    /// before the second sleep; or the sweep dropping the record so the drive never comes back.
    /// </summary>
    [Fact]
    public async Task The_ADR022_sweep_queued_ahead_of_the_suspend_keeps_the_record_and_does_not_mount()
    {
        var h = await StartedAsync(HostFixtures.OnDemand("od", probeIntervalSeconds: 60, drive: "Q:"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        Assert.Equal(MountState.Mounted, h.State("od"));

        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rcd restarting")));
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State("od"));
        Assert.Equal(1, h.MountCalls("Q:"));

        await RouteQueuedAheadOfSuspendAsync(h, () => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));

        Assert.Equal(1, h.MountCalls("Q:"));
        AssertNeverEnteredMounting(h, "od", afterMount: 1);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("od"));
        Assert.Equal(2, h.MountCalls("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 1, "the re-enable after a drain", and decision 4, "Not preempted: drains".
    /// The drain here begins INSIDE a recurring tick (the shallow-failure threshold), which is the one place
    /// a drain could inherit the tick's preemption token.
    /// <b>Catches:</b> (a) the suspend cancelling that drain's <c>mount/unmount</c> (the one thing the budget is
    /// for); (b) the drain completing while the suspend is pending and re-enabling the persistent host
    /// (<c>Probing</c>, a probe, and a mount in the seconds before sleep).
    /// </summary>
    [Fact]
    public async Task A_failure_drain_that_completes_while_a_suspend_is_pending_is_not_cancelled_and_not_re_enabled()
    {
        var h = await StartedAsync(HostFixtures.Persistent("m1", probeIntervalSeconds: 60, drive: "M:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("m1"), ShallowProbeOutcome.ConnectionRefused, times: 3);
        await h.AdvanceSecondsAsync(120);
        Assert.Equal(2, h.Snapshot("m1").ConsecutiveMountedFailures);

        var unmount = h.HRclone.HoldNext(OnCancel.LikeProduction, "unmount");
        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(unmount, "the threshold drain's mount/unmount");
        Assert.Equal(MountState.Draining, h.State("m1"));

        var suspend = SuspendCaller.Call(h.Supervisor);
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("m1"), ShallowProbeOutcome.Success);
        var probesBefore = h.ShallowCalls("m1");
        unmount.Release();

        await h.FinishAsync(pump, "the drain never finished", suspend);
        Assert.False(unmount.TokenCancelled, "the suspend cancelled a drain's mount/unmount");
        await h.AdvanceSecondsAsync(5);

        Assert.Equal([DrainCause.Automatic], h.DrainCauses("m1"));
        Assert.False(h.IsMountedInRclone("M:"));
        Assert.Equal(MountState.Disabled, h.State("m1"));
        Assert.Equal(probesBefore, h.ShallowCalls("m1"));
        Assert.Equal(1, h.MountCalls("M:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("m1"));
    }

    private static void AssertNeverEnteredMounting(PreemptSpecHarness h, string hostKey, int afterMount) =>
        Assert.Equal(afterMount, h.Transitions(hostKey).Count(t => t.To == MountState.Mounting));
}
