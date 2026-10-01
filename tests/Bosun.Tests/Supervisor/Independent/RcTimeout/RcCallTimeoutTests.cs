using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;
using Rc = Bosun.Tests.Supervisor.Independent.RcTimeout.FaultInjectingRcloneDouble;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>
/// bs-x57, items 1, 2 and 4: an rc call that fails with a cancellation-shaped exception while the
/// supervisor is NOT shutting down is an ordinary rc failure, on every path that calls rc.
/// </summary>
/// <remarks>
/// <para>
/// Each theory runs the same scenario with every <see cref="RcFaults"/> kind. The
/// <see cref="RcFaults.OrdinaryRcFailure"/> row is the control: it uses a failure the supervisor
/// already handles, so it must pass on any build. The other rows are HttpClient's timeout and two
/// other shapes of a cancellation that did not come from the supervisor's token. bs-x57 says all
/// of them must be handled exactly like the control row. That makes the control row the oracle:
/// whatever it does, the cancellation rows must do too.
/// </para>
/// <para>
/// Determinism: an injected fake clock and the <c>DrainAsync</c> pump. No sleeps, no wall clock,
/// no real rclone or drive letter. Time moves in small steps and the invariants are checked after
/// every step. "Never reaches Disabled while the drive is still mounted" is therefore checked
/// throughout the test, not only at the end.
/// </para>
/// </remarks>
public sealed class RcCallTimeoutTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    // ---------------------------------------------------------------------------------------
    // Item 1: drain step
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: a <c>mount/unmount</c> that times out during a user-requested drain is treated
    /// as shutdown. It escapes the drain step, so no retry is ever scheduled and the host stays
    /// in <c>Draining</c> forever with the drive still mounted. Six faults are used so the
    /// failures continue past the forced-unmount escalation (§4 rule 4), and the retrying has to
    /// survive that boundary as well.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task User_unmount_whose_unmount_call_fails_stays_Draining_and_retries_until_listmounts_confirms(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("vault", drive: "Q:")));
        await h.StartCleanAsync();
        await h.RunCleanAsync("RequestMountAsync(vault)", () => h.Supervisor.RequestMountAsync("vault"));
        Assert.Equal(MountState.Mounted, h.State("vault"));

        h.Rclone.FailUnmount("Q:", RcFaults.Factory(kind), times: 6);

        await h.RunCleanAsync("RequestUnmountAsync(vault) whose mount/unmount fails", () => h.Supervisor.RequestUnmountAsync("vault"));

        Assert.True(h.Rclone.IsMounted("Q:"), "Test setup: a failed unmount must leave the drive mounted.");
        Assert.Equal(MountState.Draining, h.State("vault"));
        h.AssertInvariants();

        var drained = await h.AdvanceUntilAsync(
            () => h.TransitionCount("vault", MountState.Draining, MountState.Disabled) > 0,
            Step, Patience, "advancing while vault's unmount keeps failing");

        Assert.True(
            drained,
            $"vault never left Draining in {Patience}: no retry of the failed unmount was ever scheduled." +
            $"{Environment.NewLine}{h.DumpCalls()}");
        Assert.False(h.Rclone.IsMounted("Q:"));
        Assert.True(
            h.Rclone.Count(Rc.Unmount, "Q:") >= 7,
            $"Expected the 6 failed unmounts to be retried until one succeeded; saw {h.Rclone.Count(Rc.Unmount, "Q:")} calls.");
    }

    /// <summary>
    /// The incident itself. A mounted persistent host's SSH channel dies. Two periodic deep-probe
    /// failures (ADR-016) drain it, from a TIMER-driven action, and its <c>mount/unmount</c>
    /// times out. Bug caught: the timeout escapes the timer action and the channel pump, so the
    /// supervisor goes silent (here, the pump throws). With the fix, the drain keeps retrying.
    /// When the unmount finally succeeds and listmounts confirms it, the host goes
    /// <c>Disabled</c>, and then comes back by itself, which is what a persistent host does.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Incident_replay_deep_probe_drain_whose_unmount_fails_keeps_retrying_and_the_host_returns(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(
            HostFixtures.Global(mountedDeepProbeIntervalSeconds: 300),
            HostFixtures.Persistent("traininggrounds", drive: "P:")));
        await h.StartCleanAsync();
        Assert.Equal(MountState.Mounted, h.State("traininggrounds"));

        h.Probe.Scripted.SetDeep("traininggrounds", DeepProbeOutcome.Failed, times: 2);
        h.Rclone.FailUnmount("P:", RcFaults.Factory(kind), times: 4);

        var draining = await h.AdvanceUntilAsync(
            () => h.State("traininggrounds") == MountState.Draining,
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), "advancing to the second deep-probe failure");
        Assert.True(draining, "Test setup: two deep-probe failures should have drained the host (ADR-016).");

        var disabled = await h.AdvanceUntilAsync(
            () => h.TransitionCount("traininggrounds", MountState.Draining, MountState.Disabled) > 0,
            Step, Patience, "advancing while traininggrounds' unmount keeps failing");
        Assert.True(disabled, $"The drain never completed after its unmount call recovered.{Environment.NewLine}{h.DumpCalls()}");

        var back = await h.AdvanceUntilAsync(
            () => h.State("traininggrounds") == MountState.Mounted,
            Step, Patience, "advancing until the persistent host remounts");
        Assert.True(back, $"The persistent host never came back after the drain.{Environment.NewLine}{h.DumpHistory()}");
        Assert.True(h.Rclone.IsMounted("P:"));
    }

    /// <summary>
    /// Bug caught: a <c>mount/listmounts</c> that times out while VERIFYING an unmount is treated
    /// as shutdown instead of "cannot confirm, so assume still mounted" (§4 rule 4). Here the
    /// unmount really did take effect, so a supervisor that guessed would usually guess right. The
    /// spec still forbids guessing. The host must stay <c>Draining</c> until a listmounts actually
    /// answers.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Drain_verification_whose_listmounts_call_fails_is_treated_as_still_mounted_and_retried(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("vault", drive: "Q:")));
        await h.StartCleanAsync();
        await h.RunCleanAsync("RequestMountAsync(vault)", () => h.Supervisor.RequestMountAsync("vault"));

        h.Rclone.FailListMounts(RcFaults.Factory(kind), times: 1);

        await h.RunCleanAsync("RequestUnmountAsync(vault) whose verification listmounts fails", () => h.Supervisor.RequestUnmountAsync("vault"));

        Assert.Equal(1, h.Rclone.FaultedCount(Rc.ListMounts));
        Assert.Equal(MountState.Draining, h.State("vault"));
        h.AssertInvariants();

        var drained = await h.AdvanceUntilAsync(
            () => h.TransitionCount("vault", MountState.Draining, MountState.Disabled) > 0,
            Step, Patience, "advancing after the failed verification");
        Assert.True(drained, $"vault never re-verified after the failed listmounts.{Environment.NewLine}{h.DumpCalls()}");
        Assert.True(h.Rclone.Count(Rc.ListMounts) >= 2);
    }

    // ---------------------------------------------------------------------------------------
    // Item 1: mount path
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: a <c>mount/mount</c> that times out escapes <c>TryBeginMountAsync</c>, which
    /// leaves the host in <c>Mounting</c> permanently. Nothing leaves <c>Mounting</c> except
    /// <c>Mounted</c> or <c>Draining</c>, and reconciliation skips <c>Mounting</c> hosts. The spec
    /// is to handle it as a mount failure: <c>Mounting -&gt; Draining</c>, counted in
    /// <c>ConsecutiveMountFailures</c> with a reason the tray can show (bs-ww9.4), and the host
    /// can be mounted again afterwards.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Mount_call_that_fails_is_a_mount_failure_and_never_strands_the_host_in_Mounting(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("vault", drive: "Q:")));
        await h.StartCleanAsync();
        Assert.Equal(MountState.Ready, h.State("vault"));

        h.Rclone.FailMount("Q:", RcFaults.Factory(kind), times: 1);

        var outcome = await h.TryRunAsync("RequestMountAsync(vault) whose mount/mount fails", () => h.Supervisor.RequestMountAsync("vault"));

        AssertNotStrandedInMounting(h, "vault");
        Assert.NotEqual(MountState.Mounted, h.State("vault"));
        Assert.Equal(1, h.TransitionCount("vault", MountState.Mounting, MountState.Draining));
        outcome.AssertClean();

        var snapshot = h.Snapshot("vault");
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.LastMountFailureReason));
        h.AssertInvariants();

        var ready = await h.AdvanceUntilAsync(
            () => h.State("vault") == MountState.Ready, Step, Patience, "advancing through the mount-retry pacing");
        Assert.True(ready, $"vault never returned to Ready after the failed mount.{Environment.NewLine}{h.DumpHistory()}");

        await h.RunCleanAsync("second RequestMountAsync(vault)", () => h.Supervisor.RequestMountAsync("vault"));
        Assert.Equal(MountState.Mounted, h.State("vault"));
        h.AssertInvariants();
    }

    /// <summary>
    /// Bug caught: a <c>mount/mount</c> HTTP call times out but rclone completes the mount
    /// anyway. If the timeout is treated as shutdown, the host is stuck in <c>Mounting</c> and
    /// reconciliation skips it, so a live drive letter is left with nothing supervising it. That
    /// is the Explorer-hang hazard I2 exists to prevent. As a mount failure, the drain must
    /// actually remove the late mount, and must reach <c>Disabled</c> only after listmounts shows
    /// it gone.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Mount_call_that_fails_after_rclone_actually_mounted_is_drained_not_abandoned(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("vault", drive: "Q:")));
        await h.StartCleanAsync();

        h.Rclone.FailMount("Q:", RcFaults.Factory(kind), times: 1, mountedAnyway: true);

        var outcome = await h.TryRunAsync("RequestMountAsync(vault) whose mount/mount fails late", () => h.Supervisor.RequestMountAsync("vault"));

        AssertNotStrandedInMounting(h, "vault");

        var cleared = await h.AdvanceUntilAsync(
            () => !h.Rclone.IsMounted("Q:") && h.TransitionCount("vault", MountState.Draining, MountState.Disabled) > 0,
            Step, Patience, "advancing until the late mount is cleared");
        Assert.True(
            cleared,
            $"The mount rclone completed after the HTTP call failed was never removed: Q: is still live.{Environment.NewLine}{h.DumpCalls()}");
        outcome.AssertClean();
    }

    /// <summary>
    /// Items 1 and 2 at startup. Bug caught: the first persistent host's startup
    /// <c>mount/mount</c> times out, the exception escapes <c>StartAsync</c>, and every host after
    /// it in the loop is never enabled. One host's timeout stalls the others. The failing host
    /// must take the ordinary mount-failure path, and then remount after its retry backoff.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Startup_mount_that_fails_neither_aborts_startup_nor_stalls_the_hosts_after_it(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.Persistent("alpha", drive: "P:"),
            HostFixtures.Persistent("bravo", drive: "R:")));

        h.Rclone.FailMount("P:", RcFaults.Factory(kind), times: 1);

        var outcome = await h.TryRunAsync("StartAsync whose first mount/mount fails", () => h.Supervisor.StartAsync());

        Assert.True(
            h.State("bravo") == MountState.Mounted,
            $"bravo, which comes after alpha in startup order, was left {h.State("bravo")}: alpha's failed startup " +
            "mount stopped StartAsync before it reached bravo.");
        AssertNotStrandedInMounting(h, "alpha");
        outcome.AssertClean();
        h.AssertInvariants();

        var back = await h.AdvanceUntilAsync(
            () => h.State("alpha") == MountState.Mounted, Step, Patience, "advancing through alpha's mount-retry pacing");
        Assert.True(back, $"alpha never remounted after its failed startup mount.{Environment.NewLine}{h.DumpHistory()}");
    }

    // ---------------------------------------------------------------------------------------
    // Item 1: reconciliation and re-derivation
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: the periodic reconciliation tick's <c>mount/listmounts</c> times out and the
    /// exception escapes a timer action, which kills the loop, so drift is never detected again.
    /// Not being able to ask is not evidence of drift, so nothing is drained on that tick (the
    /// control row pins this). A later tick must still catch a mount that rclone loses.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Reconciliation_listmounts_failure_skips_one_tick_and_later_ticks_still_detect_drift(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("alpha", drive: "P:")));
        await h.StartCleanAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));

        h.Rclone.FailListMounts(RcFaults.Factory(kind), times: 1);

        var ticked = await h.AdvanceUntilAsync(
            () => h.Rclone.FaultedCount(Rc.ListMounts) == 1,
            TimeSpan.FromSeconds(5), Patience, "advancing to the reconciliation tick whose listmounts fails");
        Assert.True(ticked, "Test setup: no reconciliation tick called mount/listmounts within the window.");
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(0, h.Rclone.Count(Rc.Unmount, "P:"));

        var drainsBefore = h.TransitionCount("alpha", MountState.Mounted, MountState.Draining);
        h.Rclone.DropMountSilently("P:");

        var detected = await h.AdvanceUntilAsync(
            () => h.TransitionCount("alpha", MountState.Mounted, MountState.Draining) > drainsBefore,
            TimeSpan.FromSeconds(5), Patience, "advancing until reconciliation notices the lost mount");
        Assert.True(detected, "Reconciliation never ran again after the failed tick: the lost mount went unnoticed.");
    }

    /// <summary>
    /// Bug caught: on a network change, re-derivation's <c>core/version</c> gate (ADR-017) times
    /// out and the exception is passed to the caller instead of being read as "rcd is not
    /// answering". The spec says that, when the gate fails, nothing is reconciled or drained.
    /// The command must also complete normally. A mounted host whose deep probe would fail stays
    /// <c>Mounted</c>, because acting on it without the gate is exactly what the gate prevents.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Network_change_whose_core_version_gate_fails_skips_rederivation_and_drains_nothing(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("alpha", drive: "P:")));
        await h.StartCleanAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));

        h.Rclone.FailVersion(RcFaults.Factory(kind), times: 1);
        h.Probe.Scripted.SetDeep("alpha", DeepProbeOutcome.Failed, times: 1);

        var outcome = await h.TryRunAsync("NetworkChangedAsync whose core/version fails", () => h.Supervisor.NetworkChangedAsync());

        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(0, h.Rclone.Count(Rc.Unmount, "P:"));
        outcome.AssertClean();
    }

    /// <summary>
    /// Bug caught: a network change whose <c>core/version</c> gate times out stops the whole
    /// command, so the immediate re-probe of unreachable hosts (§4 Backoff: "reset by a network
    /// address change") never happens. The host is left waiting out its backoff ladder after a
    /// dock. This pins the control row's behaviour: the gate guards re-derivation only, not the
    /// backoff reset. See the report's ambiguity note.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Network_change_whose_core_version_gate_fails_still_reprobes_unreachable_hosts(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("bravo", drive: "R:")));
        h.Probe.Scripted.SetShallow("bravo.example.internal", ShallowProbeOutcome.Timeout, times: 1);
        await h.StartCleanAsync();
        Assert.Equal(MountState.Unreachable, h.State("bravo"));
        var probesBefore = h.Probe.Scripted.ShallowCount("bravo.example.internal");

        h.Rclone.FailVersion(RcFaults.Factory(kind), times: 1);

        var outcome = await h.TryRunAsync("NetworkChangedAsync whose core/version fails", () => h.Supervisor.NetworkChangedAsync());

        Assert.True(
            h.Probe.Scripted.ShallowCount("bravo.example.internal") > probesBefore,
            "bravo was not re-probed on the network change: the failed core/version gate aborted the backoff reset too.");
        Assert.NotEqual(MountState.Unreachable, h.State("bravo"));
        outcome.AssertClean();
        h.AssertInvariants();
    }

    /// <summary>
    /// Acceptance test T1 (overnight sleep) with a slow <c>rclone rcd</c>. On resume rcd often
    /// has not finished waking, so <c>core/version</c> is exactly the call likely to time out.
    /// Bug caught: that timeout escapes <c>ResumeAsync</c> before it re-enables the hosts
    /// suspend drained, so nothing ever remounts without user action. With the gate failing,
    /// re-derivation is skipped, but "everything else previously enabled goes to Probing"
    /// (§4 rule 5) still happens.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task Resume_whose_core_version_gate_fails_still_re_enables_hosts_suspend_drained(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("alpha", drive: "P:")));
        await h.StartCleanAsync();
        await h.RunCleanAsync("SuspendAsync", () => h.Supervisor.SuspendAsync());
        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.False(h.Rclone.IsMounted("P:"));

        h.Rclone.FailVersion(RcFaults.Factory(kind), times: 1);

        var outcome = await h.TryRunAsync("ResumeAsync whose core/version fails", () => h.Supervisor.ResumeAsync());

        Assert.True(
            h.State("alpha") == MountState.Mounted,
            $"alpha was left {h.State("alpha")} after resume: the failed core/version gate stopped ResumeAsync " +
            "before it re-enabled the hosts suspend had drained, so nothing remounts without user action.");
        outcome.AssertClean();
        h.AssertInvariants();
    }

    // ---------------------------------------------------------------------------------------
    // Item 2: one host's timeout must not stall another's queued work
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: alpha's timer-driven drain hits a timed-out <c>mount/unmount</c>, and the
    /// exception takes the pump down with it. bravo's mount request, already queued behind
    /// alpha's action, never runs. In production this is "the supervisor went permanently silent
    /// for every host".
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task A_failed_rc_call_in_one_hosts_timer_driven_drain_does_not_stop_queued_work_for_another(string kind)
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(
            HostFixtures.Global(mountedDeepProbeIntervalSeconds: 300),
            HostFixtures.Persistent("alpha", drive: "P:"),
            HostFixtures.OnDemand("bravo", drive: "Q:")));
        await h.StartCleanAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(MountState.Ready, h.State("bravo"));

        h.Probe.Scripted.SetDeep("alpha", DeepProbeOutcome.Failed, times: 2);
        h.Rclone.FailUnmount("P:", RcFaults.Factory(kind), times: 1);

        await h.AdvanceCleanAsync(TimeSpan.FromSeconds(300), "alpha's first deep-probe failure");
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(1, h.Snapshot("alpha").ConsecutiveDeepProbeFailures);

        // Fire alpha's second deep probe WITHOUT pumping, so its drain action is queued first.
        // Then queue bravo's request behind it, and only then pump.
        h.Time.Advance(TimeSpan.FromSeconds(300));
        var bravoMount = h.Supervisor.RequestMountAsync("bravo");
        var pumpFault = await Record.ExceptionAsync(() => h.Supervisor.DrainAsync());

        Assert.Equal(MountState.Draining, h.State("alpha"));
        Assert.True(
            bravoMount.IsCompleted,
            "bravo's mount request, queued behind alpha's drain, never ran" +
            (pumpFault is null ? "." : $": {PumpOutcome.Describe(pumpFault)} from alpha's unmount escaped the pump first."));
        await bravoMount;
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        Assert.Null(pumpFault);
        h.AssertInvariants();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance criterion: "is logged"
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: a timed-out rc call is handled, or escapes, without leaving a trace in the
    /// log. The incident was diagnosable only by noticing the log had stopped. This requires at
    /// least one Warning-or-higher entry that carries the timeout exception itself.
    /// </summary>
    [Fact]
    public async Task A_timed_out_unmount_is_logged_at_warning_or_above_with_the_exception_attached()
    {
        var h = new RcTimeoutHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("vault", drive: "Q:")));
        await h.StartCleanAsync();
        await h.RunCleanAsync("RequestMountAsync(vault)", () => h.Supervisor.RequestMountAsync("vault"));

        h.Rclone.FailUnmount("Q:", RcFaults.Factory(RcFaults.HttpClientTimeout), times: 1);
        await h.TryRunAsync("RequestUnmountAsync(vault)", () => h.Supervisor.RequestUnmountAsync("vault"));

        Assert.True(
            h.Logger.Entries.Any(e => e.Level >= LogLevel.Warning && CarriesHttpTimeout(e.Exception)),
            "No Warning-or-higher log entry carries the timed-out unmount's exception. Logged at Warning or above:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, h.Logger.Entries
                .Where(e => e.Level >= LogLevel.Warning)
                .Select(e => $"  [{e.Level}] {e.Message} ({e.Exception?.GetType().Name ?? "no exception"})")));
    }

    private static void AssertNotStrandedInMounting(RcTimeoutHarness h, string hostKey) =>
        Assert.True(
            h.State(hostKey) != MountState.Mounting,
            $"{hostKey} is stranded in Mounting after its mount/mount failed. Only Mounted or Draining leave Mounting, " +
            $"and reconciliation skips Mounting hosts, so nothing will ever move it again.{Environment.NewLine}{h.DumpHistory()}");

    /// <summary>True when <paramref name="ex"/> is, or wraps, the HttpClient-timeout exception. A
    /// fix may wrap the timeout in its own type before logging it, and that is acceptable.</summary>
    private static bool CarriesHttpTimeout(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is TaskCanceledException { InnerException: TimeoutException })
            {
                return true;
            }
        }

        return false;
    }
}
