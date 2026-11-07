using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.SystemEventIntegration;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// ADR-023 (bs-4in, bs-7d7, bs-4ca): a suspend preempts routine checks. Invariant I8 gives the machine
/// roughly three seconds to have its drives unmounted; a suspend queued behind a hung probe or rc call
/// used to wait for it. These tests pin what the design says happens instead: the I8 gate closes on the
/// caller's thread, the running recurring tick is cancelled and its result discarded (Success included)
/// without touching a counter, and everything that is NOT a recurring tick is left alone.
/// </summary>
/// <remarks>
/// Fakes and the fake clock only. A "hung" call is a call the test holds open; nothing sleeps and no
/// time passes unless a test advances the clock. <see cref="PreemptionHarness.Guard"/> is only a hang
/// guard so a regression fails the test instead of the run.
/// </remarks>
public sealed class SuspendPreemptionTests
{
    private const int Budget = 3;

    private static BosunConfig Config(int failuresBeforeUnmount, params HostConfig[] hosts) =>
        HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: failuresBeforeUnmount, suspendUnmountTimeoutSeconds: Budget), hosts);

    private static BosunConfig Config(params HostConfig[] hosts) => Config(3, hosts);

    private static string Hostname(string key) => PreemptionHarness.Hostname(key);

    private static DrainCause[] Causes(params DrainCause[] causes) => causes;

    private static async Task<PreemptionHarness> MountedAsync(BosunConfig config, params string[] hostsToClick)
    {
        var h = new PreemptionHarness(config);
        await h.StartAsync();
        foreach (var key in hostsToClick)
        {
            await h.RunAsync(() => h.Supervisor.RequestMountAsync(key));
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        return h;
    }

    // ------------------------------------------------------------------------------------------
    // 1. A hung call is cut short, and the suspend is served at arrival
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> Invariant I8's budget. A mounted-probe tick that is hung in its VFS sample (the
    /// 2 x probe-timeout case bs-4in was filed for) must not delay the suspend: the suspend is served
    /// the moment it arrives, with no clock advance at all, so the 5 s probe budget could not have been
    /// what released it.
    /// <b>Catches:</b> a suspend that waits for the hung call (the guard fires), a suspend that cancels
    /// nothing, and a preempted sample that is booked as a failed sample.
    /// </summary>
    [Fact]
    public async Task A_hung_VFS_sample_is_cut_short_and_the_suspend_unmounts_once_at_arrival_with_the_clock_unmoved()
    {
        var h = await MountedAsync(Config(HostFixtures.OnDemand("archive", idleUnmountSeconds: 600, drive: "Q:")), "archive");
        h.RcloneFake.HangVfsCalls = true;
        var vfsCallsBefore = h.RcloneFake.VfsCallTotal;

        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.Pump();
        await h.RcloneFake.WaitForVfsCallAsync(vfsCallsBefore + 1).WaitAsync(PreemptionHarness.Guard);
        var arrival = h.Time.GetUtcNow();

        await h.SuspendDuringAsync(pump);

        Assert.Equal(arrival, h.Time.GetUtcNow());
        Assert.Equal(MountState.Disabled, h.State("archive"));
        Assert.Equal(1, h.RcloneFake.UnmountCallCountFor("Q:"));
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("archive"));
        Assert.Equal(arrival, h.Transitions("archive").Last(t => t.To == MountState.Draining).TimestampUtc);
        Assert.Equal(vfsCallsBefore + 1, h.RcloneFake.VfsCallTotal); // the sample was cut at its first call
        Assert.Equal(1, h.InformationLines("mounted VFS sample tick"));
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("VFS", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------
    // 2. A preempted shallow probe is not a failed probe
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "decide by the token, never by the outcome or the exception type". With
    /// <c>failures_before_unmount = 1</c> a single counted failure would drain the host as Automatic
    /// (a toast), so any reading of the probe's outcome shows up as the wrong drain cause here.
    /// <b>Catches:</b> the naive fix that links the token but still counts <c>Cancelled</c> (which
    /// <c>HostProbe</c> returns instead of throwing) or the resulting exception as a failure.
    /// </summary>
    [Theory]
    [InlineData(HoldMode.ReturnCancelled)]
    [InlineData(HoldMode.ThrowOnCancel)]
    public async Task A_preempted_shallow_probe_one_failure_from_the_threshold_drains_as_Suspend_and_remounts_after_resume(HoldMode mode)
    {
        var h = await MountedAsync(Config(failuresBeforeUnmount: 1, HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:")));
        var held = h.Gate.HoldNextShallow(Hostname("prod"), mode);

        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await h.SuspendDuringAsync(pump);

        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("prod"));
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(1, h.InformationLines("mounted shallow probe tick"));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(2, h.RcloneFake.MountCalls.Count);
    }

    /// <summary>
    /// <b>Protects:</b> "discard the result, Success included". A probe that finishes normally just as
    /// the suspend arrives must not carry on into the tick's follow-on work. For an on-demand host with an
    /// idle clock that follow-on work is the VFS sample, so a sample call after the suspend is the tell.
    /// <b>Catches:</b> a tick that checks the token only on failure.
    /// </summary>
    [Fact]
    public async Task A_shallow_probe_that_succeeds_after_the_suspend_arrived_is_discarded()
    {
        var h = await MountedAsync(Config(HostFixtures.OnDemand("archive", idleUnmountSeconds: 600, drive: "Q:")), "archive");
        var held = h.Gate.HoldNextShallow(Hostname("archive"), HoldMode.IgnoreToken);
        var vfsCallsBefore = h.RcloneFake.VfsCallTotal;

        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        var suspend = h.Supervisor.SuspendAsync();
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(vfsCallsBefore, h.RcloneFake.VfsCallTotal);
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("archive"));
    }

    // ------------------------------------------------------------------------------------------
    // 3. The same for the deep probe, whose counter has a fixed threshold of 2
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> ADR-016's <c>ConsecutiveDeepProbeFailures</c> against a preempted deep probe. The
    /// host is primed one failure short of the fixed threshold (2); a second, cancelled one must not
    /// reach it.
    /// <b>Catches:</b> a deep tick that counts a cancelled probe, draining a healthy mount as Automatic
    /// every time the machine sleeps.
    /// </summary>
    [Theory]
    [InlineData(HoldMode.ReturnCancelled)]
    [InlineData(HoldMode.ThrowOnCancel)]
    public async Task A_preempted_deep_probe_one_failure_from_the_threshold_drains_as_Suspend_and_remounts_after_resume(HoldMode mode)
    {
        var h = await MountedAsync(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:")));
        h.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed);
        await h.AdvanceAsync(TimeSpan.FromSeconds(300)); // the first periodic deep probe fails
        Assert.Equal(1, h.Snapshot("prod").ConsecutiveDeepProbeFailures);
        Assert.Equal(MountState.Mounted, h.State("prod"));

        var held = h.Gate.HoldNextDeep("prod", mode);
        h.FireTimers(TimeSpan.FromSeconds(300)); // the next deep probe is due
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await h.SuspendDuringAsync(pump);

        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("prod"));
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(1, h.InformationLines("mounted deep probe tick"));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("prod"));
    }

    // ------------------------------------------------------------------------------------------
    // 4. A preempted idle fire
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> a suspend that lands while the idle-unmount fire is reading the VFS. The drain
    /// is a Suspend drain, not an IdleTimeout one, and that is what makes the user's on-demand drive come
    /// back at wake (ADR-022): an IdleTimeout drain would not. <see cref="HoldMode.IgnoreToken"/> is the
    /// dangerous case, a read that completes cleanly with "no activity" after the suspend arrived.
    /// <b>Catches:</b> an idle fire that acts on a result it should have discarded.
    /// </summary>
    [Theory]
    [InlineData(HoldMode.ThrowOnCancel)]
    [InlineData(HoldMode.IgnoreToken)]
    public async Task A_preempted_idle_fire_drains_as_Suspend_not_IdleTimeout_and_the_drive_returns_at_wake(HoldMode mode)
    {
        var h = await MountedAsync(Config(HostFixtures.OnDemand("archive", idleUnmountSeconds: 30, drive: "Q:")), "archive");
        var held = h.Rclone.HoldNextVfsList(mode);

        h.FireTimers(TimeSpan.FromSeconds(30)); // the idle clock runs out
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);

        var suspend = h.Supervisor.SuspendAsync();
        if (mode == HoldMode.IgnoreToken)
        {
            held.Release();
        }

        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("archive"));
        Assert.Equal(1, h.RcloneFake.UnmountCallCountFor("Q:"));
        Assert.Equal(1, h.InformationLines("idle-unmount tick"));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("archive"));
    }

    // ------------------------------------------------------------------------------------------
    // 5. Queued ticks do no I/O
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "a tick that starts while a suspend is pending sees it, cancels itself and skips
    /// its I/O". Every kind of recurring tick is queued ahead of the suspend; none may probe, read the
    /// VFS, call core/version, or reconcile.
    /// <b>Catches:</b> a tick that only checks the token after its first awaited call, and any of the
    /// five kinds being left off the preemptible path.
    /// </summary>
    [Fact]
    public async Task Ticks_queued_ahead_of_a_pending_suspend_do_no_I_O()
    {
        var persistentDark = HostFixtures.Persistent("dark", probeIntervalSeconds: 30, drive: "D:");
        var h = new PreemptionHarness(Config(
            HostFixtures.OnDemand("archive", idleUnmountSeconds: 30, drive: "Q:"),
            HostFixtures.OnDemand("ready", probeIntervalSeconds: 30, drive: "R:"),
            persistentDark));
        h.Probe.EnqueueShallow(Hostname("dark"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, h.State("archive"));
        Assert.Equal(MountState.Ready, h.State("ready"));
        Assert.Equal(MountState.Unreachable, h.State("dark"));

        var shallow = h.Probe.ShallowProbeCalls.Count;
        var deep = h.Probe.DeepProbeCalls.Count;
        var vfs = h.RcloneFake.VfsCallTotal;
        var version = h.RcloneFake.GetVersionCallCount;
        var listMounts = h.RcloneFake.ListMountsCallCount;
        var mounts = h.RcloneFake.MountCalls.Count;

        // Queue one of each kind: reconciliation, the idle probe of a Ready and of an Unreachable host,
        // the mounted shallow and deep probes, and the idle-unmount fire.
        h.FireTimers(TimeSpan.FromSeconds(300));
        var suspend = h.Supervisor.SuspendAsync();
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(shallow, h.Probe.ShallowProbeCalls.Count);
        Assert.Equal(deep, h.Probe.DeepProbeCalls.Count);
        Assert.Equal(vfs, h.RcloneFake.VfsCallTotal);
        Assert.Equal(version, h.RcloneFake.GetVersionCallCount);
        Assert.Equal(listMounts + 1, h.RcloneFake.ListMountsCallCount); // only the suspend drain's own verification
        Assert.Equal(mounts, h.RcloneFake.MountCalls.Count);
        foreach (var kind in new[] { "idle probe", "mounted shallow probe", "mounted deep probe", "idle-unmount", "reconciliation" })
        {
            Assert.True(h.InformationLines($"The {kind} tick") >= 1, $"no preempted '{kind}' tick was logged");
        }
    }

    // ------------------------------------------------------------------------------------------
    // 6. Nothing queued ahead of a pending suspend mounts
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the I8 gate closing on the caller's thread. A user's mount click and a
    /// persistent host's auto-mount tick are both queued BEFORE the suspend; until the suspend is
    /// dequeued the old gate (<c>suspended</c>) was still open, and both mounted.
    /// <b>Catches:</b> any gate that reads only the flag the suspend action sets.
    /// </summary>
    [Fact]
    public async Task Nothing_queued_ahead_of_a_pending_suspend_mounts()
    {
        var h = new PreemptionHarness(Config(
            HostFixtures.OnDemand("archive", drive: "Q:"),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        h.Probe.EnqueueShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("prod"));

        var click = h.Supervisor.RequestMountAsync("archive");
        h.FireTimers(TimeSpan.FromSeconds(5)); // prod's first backoff rung: its idle tick would probe, reach Ready, auto-mount
        var suspend = h.Supervisor.SuspendAsync();
        await h.Pump();

        var refused = await Assert.ThrowsAsync<MountRequestRefusedException>(() => click.WaitAsync(PreemptionHarness.Guard));
        Assert.Contains("suspended", refused.Message, StringComparison.OrdinalIgnoreCase);
        await suspend.WaitAsync(PreemptionHarness.Guard);
        Assert.Empty(h.RcloneFake.MountCalls);
        Assert.Equal(MountState.Ready, h.State("archive"));
        Assert.Equal(MountState.Unreachable, h.State("prod"));

        // Not vacuous: both hosts are mountable, and resume brings the persistent one up.
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Single(h.RcloneFake.MountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> the same gate on the COMMAND routes, which are not preempted: a "retry now"
    /// queued ahead of the suspend still probes, takes the persistent host to Ready, and must stop there.
    /// "Retry now" is a single-host command, so it has no next host to stop before (bs-5ga.1); the
    /// multi-host commands, which do stop between hosts, are pinned in
    /// <see cref="SuspendStopsMultiHostCommandsTests"/>. What stays true for those as well is that the
    /// call in flight is never cancelled.
    /// <b>Catches:</b> a gate placed only on the tick path.
    /// </summary>
    [Fact]
    public async Task Commands_queued_ahead_of_a_pending_suspend_probe_but_do_not_mount()
    {
        var h = new PreemptionHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        h.Probe.EnqueueShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("prod"));

        var shallowBefore = h.Probe.ShallowProbeCalls.Count;
        var retry = h.Supervisor.RequestRetryNowAsync("prod");
        var suspend = h.Supervisor.SuspendAsync();
        await h.Pump();
        await retry.WaitAsync(PreemptionHarness.Guard);
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(shallowBefore + 1, h.Probe.ShallowProbeCalls.Count); // commands are not preempted
        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.Empty(h.RcloneFake.MountCalls);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
    }

    // ------------------------------------------------------------------------------------------
    // 7. suspend, resume, suspend
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the counter (not a flag). A resume queued between two suspends runs while the
    /// second is pending, so it must clear <c>suspended</c> without opening the gate or enabling any
    /// host. This is also why a priority lane for suspend was rejected: it would have run the second
    /// suspend before the resume it follows.
    /// <b>Catches:</b> a boolean pending flag, and an <c>EnableHostAsync</c> with no early return.
    /// </summary>
    [Fact]
    public async Task A_resume_queued_ahead_of_a_second_suspend_mounts_nothing()
    {
        var h = await MountedAsync(Config(
            HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:"),
            HostFixtures.OnDemand("archive", drive: "Q:")), "archive");
        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(MountState.Disabled, h.State("archive"));
        var mounts = h.RcloneFake.MountCalls.Count;
        var shallow = h.Probe.ShallowProbeCalls.Count;

        var resume = h.Supervisor.ResumeAsync();
        var secondSuspend = h.Supervisor.SuspendAsync();
        await h.Pump();
        await resume.WaitAsync(PreemptionHarness.Guard);
        await secondSuspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(mounts, h.RcloneFake.MountCalls.Count);
        Assert.Equal(shallow, h.Probe.ShallowProbeCalls.Count); // not even the enable probe
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(MountState.Disabled, h.State("archive"));

        // Not vacuous: the final resume brings both back, the on-demand one through its ADR-022 record.
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(MountState.Mounted, h.State("archive"));
    }

    // ------------------------------------------------------------------------------------------
    // 8. What is NOT preempted
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "drains are not preempted", including a drain started by a tick that is itself
    /// being preempted. An idle-unmount fire reads a quiet VFS and starts an IdleTimeout drain; its
    /// <c>mount/unmount</c> is in flight when the suspend arrives and must finish, not be cancelled.
    /// <b>Catches:</b> a drain handed the tick's preemption token instead of the supervisor's own.
    /// </summary>
    [Fact]
    public async Task A_drain_in_progress_is_not_cancelled_even_when_a_preempted_tick_started_it()
    {
        var h = await MountedAsync(Config(HostFixtures.OnDemand("archive", idleUnmountSeconds: 30, drive: "Q:")), "archive");
        var held = h.Rclone.HoldNextUnmount(HoldMode.ThrowOnCancel);

        h.FireTimers(TimeSpan.FromSeconds(30));
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        Assert.Equal(MountState.Draining, h.State("archive"));

        var suspend = h.Supervisor.SuspendAsync();
        // TokenLineage, not the token's own flag: the suspend's CancelAsync reaches a linked token only
        // when the thread pool runs the link, so "not cancelled yet" would prove nothing (bs-s30).
        Assert.False(TokenLineage.CancellationRequested(held.Token), "a suspend must not cancel a drain's mount/unmount");
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(MountState.Disabled, h.State("archive"));
        Assert.Equal(Causes(DrainCause.IdleTimeout), h.DrainCauses("archive"));
        Assert.All(h.Rclone.UnmountTokens, token => Assert.False(TokenLineage.CancellationRequested(token)));
        Assert.Equal(1, h.RcloneFake.UnmountCallCountFor("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> "mounts in progress are not cancelled". A persistent host's auto-mount runs inside
    /// an idle-probe tick; its deep probe must not be handed that tick's preemption token. If it were,
    /// the cancelled probe would be read as a deep-probe failure and the mount booked as a failure.
    /// <b>Catches:</b> the preemption token leaking into <c>TryBeginMountAsync</c>.
    /// </summary>
    [Fact]
    public async Task The_deep_probe_of_a_mount_started_inside_a_tick_is_not_cancelled_and_the_mount_is_abandoned_cleanly()
    {
        var h = new PreemptionHarness(Config(HostFixtures.Persistent("dark", probeIntervalSeconds: 30, drive: "D:")));
        h.Probe.EnqueueShallow(Hostname("dark"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("dark"));
        var held = h.Gate.HoldNextDeep("dark", HoldMode.ReturnCancelled);

        h.FireTimers(TimeSpan.FromSeconds(5)); // the idle tick: probe passes, Ready, auto-mount, deep probe starts
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        Assert.Equal(MountState.Mounting, h.State("dark"));

        var suspend = h.Supervisor.SuspendAsync();
        // TokenLineage, not the token's own flag: see the drain test above (bs-s30).
        Assert.False(TokenLineage.CancellationRequested(held.Token), "a suspend must not cancel the deep probe of a mount");
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Empty(h.RcloneFake.MountCalls);
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("dark"));
        Assert.Equal(0, h.Snapshot("dark").ConsecutiveMountFailures);
    }

    // ------------------------------------------------------------------------------------------
    // 9. The prologue never throws on the caller's thread
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the SystemEvents thread. The prologue of <c>SuspendAsync</c> runs on a thread
    /// with no <c>catch</c> above it, so a cancellation callback that throws must neither escape the call
    /// nor stop the suspend, and the fault must be logged. A callback that runs inline would run on the
    /// caller's thread; it must run on a pool thread.
    /// <b>Catches:</b> <c>Cancel()</c> in place of <c>CancelAsync()</c> (the callback would run on the
    /// caller's thread, and its exception would surface there or be logged as a different failure), a
    /// prologue with no handling, and a fault that is swallowed unlogged.
    /// </summary>
    [Fact]
    public async Task The_prologue_does_not_throw_on_the_callers_thread_when_a_cancellation_callback_throws()
    {
        var h = await MountedAsync(Config(failuresBeforeUnmount: 1, HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:")));
        var held = h.Gate.HoldNextShallow(Hostname("prod"), HoldMode.ReturnCancelled, throwingCallback: true);
        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);

        Task? suspend = null;
        var callerThread = Environment.CurrentManagedThreadId;
        var thrown = Record.Exception(() => { suspend = h.Supervisor.SuspendAsync(); });

        Assert.Null(thrown);
        await h.Log.WhenLoggedAsync(e => e.Level == LogLevel.Warning && e.Message.Contains("Cancelling the running tick")).WaitAsync(PreemptionHarness.Guard);
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend!.WaitAsync(PreemptionHarness.Guard);

        Assert.NotEqual(0, held.CancellationCallbackThreadId);
        Assert.NotEqual(callerThread, held.CancellationCallbackThreadId);
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("prod"));
    }

    /// <summary>
    /// <b>Protects:</b> a suspend with no tick running is the common case and must not need one.
    /// <b>Catches:</b> a prologue that assumes a published tick.
    /// </summary>
    [Fact]
    public async Task A_suspend_with_no_tick_running_is_served_normally()
    {
        var h = await MountedAsync(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:")));

        await h.SuspendAsync();

        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("prod"));
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ------------------------------------------------------------------------------------------
    // 10. End to end through the system-event adapter
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the whole path Windows takes: the adapter calls <c>SuspendAsync</c> on the
    /// notification thread and waits for it against the budget. With the real supervisor loop running and
    /// a VFS sample hung, the drain must confirm inside the budget with the fake clock unmoved. The
    /// adapter's own budget delay runs on that same fake clock, so if the suspend had to wait for the
    /// hung call this would never complete (the guard fires).
    /// <b>Catches:</b> a regression anywhere between the adapter and the tick, including the cancel not
    /// reaching a tick that is running on the real loop's thread.
    /// </summary>
    [Fact]
    public async Task End_to_end_through_the_adapter_the_drain_confirms_inside_the_budget_with_the_clock_unmoved()
    {
        var config = Config(HostFixtures.OnDemand("archive", idleUnmountSeconds: 600, drive: "Q:"));
        var h = await MountedAsync(config, "archive");
        var adapter = new SystemEventSupervisorAdapter(
            new FakeSystemEventSource(), h.Supervisor, new FakeHostConfigStore(config), h.Time,
            NullLogger<SystemEventSupervisorAdapter>.Instance);
        using var shutdown = new CancellationTokenSource();
        var loop = h.Supervisor.RunAsync(shutdown.Token);

        h.RcloneFake.HangVfsCalls = true;
        var vfsCallsBefore = h.RcloneFake.VfsCallTotal;
        h.FireTimers(TimeSpan.FromSeconds(60));
        await h.RcloneFake.WaitForVfsCallAsync(vfsCallsBefore + 1).WaitAsync(PreemptionHarness.Guard);
        var before = h.Time.GetUtcNow();

        var confirmedInsideBudget = await adapter.WaitForSuspendDrainAsync().WaitAsync(PreemptionHarness.Guard);

        Assert.True(confirmedInsideBudget);
        Assert.Equal(before, h.Time.GetUtcNow());
        Assert.Equal(MountState.Disabled, h.State("archive"));
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("archive"));
        Assert.Equal(1, h.RcloneFake.UnmountCallCountFor("Q:"));

        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(PreemptionHarness.Guard));
    }

    // ------------------------------------------------------------------------------------------
    // D1: a mount whose deep probe passed with a suspend pending is abandoned
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision D1. The deep probe is not preempted, so a suspend can arrive during it;
    /// when it passes, <c>mount/mount</c> must not be sent. The mount is abandoned on the existing
    /// <c>Mounting -&gt; Draining</c> edge as a Suspend drain, it is not counted as a mount failure, and
    /// the user's on-demand drive returns at wake (ADR-022).
    /// <b>Catches:</b> a mount sent after the suspend arrived, an abandonment booked as MountFailure
    /// (a paced retry, a "mount failed" row), and an on-demand host that loses the user's intent.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_mount_whose_deep_probe_passes_with_a_suspend_pending_is_abandoned_before_mount_mount(bool onDemand)
    {
        var host = onDemand
            ? HostFixtures.OnDemand("h", drive: "Q:")
            : HostFixtures.Persistent("h", probeIntervalSeconds: 0, drive: "Q:");
        var h = new PreemptionHarness(Config(host));
        if (onDemand)
        {
            await h.StartAsync(); // an on-demand host rests in Ready until the click
        }

        var held = h.Gate.HoldNextDeep("h", HoldMode.IgnoreToken);

        // A persistent host auto-mounts as startup brings it to Ready; an on-demand one on the click.
        var trigger = onDemand ? h.Supervisor.RequestMountAsync("h") : h.Supervisor.StartAsync();
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        Assert.Equal(MountState.Mounting, h.State("h"));

        var suspend = h.Supervisor.SuspendAsync();
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);
        await trigger.WaitAsync(PreemptionHarness.Guard);

        Assert.Empty(h.RcloneFake.MountCalls);
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("h"));
        Assert.Equal(MountState.Disabled, h.State("h"));
        var snapshot = h.Snapshot("h");
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("h")); // the persistent tier, or the ADR-022 record
        Assert.Single(h.RcloneFake.MountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> "a deep-probe failure still wins over this". A deep probe that FAILS with a
    /// suspend pending is a genuine failure of the mount attempt and is booked as one.
    /// <b>Catches:</b> a D1 check placed before the failure branch, hiding a real failure behind a suspend.
    /// </summary>
    [Fact]
    public async Task A_deep_probe_that_fails_with_a_suspend_pending_is_still_a_mount_failure()
    {
        var h = new PreemptionHarness(Config(HostFixtures.OnDemand("archive", drive: "Q:")));
        await h.StartAsync();
        var held = h.Gate.HoldNextDeep("archive", HoldMode.IgnoreToken, releaseWith: DeepProbeOutcome.Failed);
        var click = h.Supervisor.RequestMountAsync("archive");
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);

        var suspend = h.Supervisor.SuspendAsync();
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);
        await click.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(Causes(DrainCause.MountFailure), h.DrainCauses("archive"));
        Assert.Equal(1, h.Snapshot("archive").ConsecutiveMountFailures);
        Assert.Empty(h.RcloneFake.MountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> "skip the post-mount VFS baseline sample while a suspend is pending". A mount
    /// whose <c>mount/mount</c> was already on the wire completes, but the sample that follows it would
    /// be an unpreemptible poll against a possibly hung rcd, for a drive that is about to be drained.
    /// <b>Catches:</b> the baseline sample running regardless. The first half is the control: without a
    /// suspend the same mount does sample.
    /// </summary>
    [Fact]
    public async Task The_baseline_VFS_sample_after_a_mount_is_skipped_while_a_suspend_is_pending()
    {
        var h = new PreemptionHarness(Config(HostFixtures.OnDemand("archive", idleUnmountSeconds: 600, drive: "Q:")));
        await h.StartAsync();
        var vfsBeforeFirstMount = h.RcloneFake.VfsCallTotal;
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));
        Assert.True(h.RcloneFake.VfsCallTotal > vfsBeforeFirstMount, "control: an ordinary mount takes its baseline sample");
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("archive"));
        Assert.Equal(MountState.Ready, h.State("archive")); // drained, and re-enabled back to Ready (the user's click was the unmount)
        var vfsBeforeSecondMount = h.RcloneFake.VfsCallTotal;

        var held = h.Rclone.HoldNextMount();
        var click = h.Supervisor.RequestMountAsync("archive");
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        var suspend = h.Supervisor.SuspendAsync();
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);
        await click.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(2, h.RcloneFake.MountCalls.Count); // the mount on the wire was allowed to finish
        Assert.Equal(vfsBeforeSecondMount, h.RcloneFake.VfsCallTotal);
        Assert.Equal(MountState.Disabled, h.State("archive"));
    }

    // ------------------------------------------------------------------------------------------
    // D5: idle-probe ticks skip while suspended
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision D5. While the system is suspended an idle-probe TICK does nothing,
    /// matching the mount-retry timer; resume probes every such host at once. Forced probes (a network
    /// change, "retry now") are commands and still probe.
    /// <b>Catches:</b> an idle tick that probes during the suspend, and a skip that is applied to the
    /// forced probes too (which share the handler).
    /// </summary>
    [Fact]
    public async Task Idle_probe_ticks_skip_while_suspended_but_forced_probes_still_run()
    {
        var h = new PreemptionHarness(Config(HostFixtures.Persistent("dark", probeIntervalSeconds: 30, drive: "D:")));
        h.Probe.EnqueueShallow(Hostname("dark"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("dark"));
        await h.SuspendAsync();
        var shallow = h.Probe.ShallowProbeCalls.Count;

        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(shallow, h.Probe.ShallowProbeCalls.Count);
        Assert.Equal(MountState.Unreachable, h.State("dark"));
        Assert.True(h.InformationLines("Idle probe tick for dark skipped") >= 1);
        Assert.Empty(h.RcloneFake.MountCalls);

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        Assert.Equal(shallow + 1, h.Probe.ShallowProbeCalls.Count); // a command is not a tick
        Assert.Equal(MountState.Ready, h.State("dark"));
        Assert.Empty(h.RcloneFake.MountCalls);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("dark"));
    }

    /// <summary>
    /// <b>Protects:</b> a tick preempted by a suspend discards even a SUCCESSFUL probe: an Unreachable
    /// persistent host whose idle probe comes back Success just as the suspend arrives stays
    /// Unreachable, with its backoff untouched, and its timer is re-armed (it fires again, and skips).
    /// <b>Catches:</b> an idle tick that acts on a Success, and one that forgets to re-arm.
    /// </summary>
    [Fact]
    public async Task A_preempted_idle_probe_discards_a_success_changes_no_backoff_and_re_arms_its_timer()
    {
        var h = new PreemptionHarness(Config(HostFixtures.Persistent("dark", probeIntervalSeconds: 30, drive: "D:")));
        h.Probe.EnqueueShallow(Hostname("dark"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        var failures = h.Snapshot("dark").ConsecutiveIdleFailures;
        var held = h.Gate.HoldNextShallow(Hostname("dark"), HoldMode.IgnoreToken);

        h.FireTimers(TimeSpan.FromSeconds(5));
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        var suspend = h.Supervisor.SuspendAsync();
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(MountState.Unreachable, h.State("dark"));
        Assert.Equal(failures, h.Snapshot("dark").ConsecutiveIdleFailures);
        Assert.Empty(h.RcloneFake.MountCalls);
        Assert.Equal(1, h.InformationLines("The idle probe tick for dark was preempted"));

        // Re-armed: the next rung fires, and (suspended) skips rather than probing.
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.True(h.InformationLines("Idle probe tick for dark skipped") >= 1);
    }

    // ------------------------------------------------------------------------------------------
    // bs-7d7: the budget counts from arrival
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> bs-7d7 / rule 5, "counted from the moment suspend arrives". The suspend waits in
    /// the channel for more than the whole budget; when it is finally dequeued its drain has already used
    /// that time, so the first unconfirmed verify escalates at once, at the dequeue time and with no
    /// further clock advance.
    /// <b>Catches:</b> a drain deadline taken from the dequeue time (the old behaviour), which would wait
    /// another full budget.
    /// </summary>
    [Fact]
    public async Task A_suspend_that_waited_longer_than_the_budget_escalates_its_drain_at_once()
    {
        var h = await MountedAsync(Config(HostFixtures.OnDemand("archive", drive: "Q:")), "archive");
        h.RcloneFake.ConfirmUnmountAfterCall("Q:", int.MaxValue);

        var suspend = h.Supervisor.SuspendAsync(); // arrival
        h.FireTimers(TimeSpan.FromSeconds(Budget + 1)); // it waits past its whole budget
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(2, h.RcloneFake.UnmountCallCountFor("Q:")); // the drain, and its forced re-issue now
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("escalating to forced unmount"));
    }

    /// <summary>
    /// <b>Protects:</b> the same for a host that was ALREADY draining when the suspend arrived: its
    /// shortened deadline is arrival + budget, not dequeue time + budget.
    /// <b>Catches:</b> <c>ApplySuspendBudgetToDrainingHostAsync</c> reading the clock when it runs.
    /// </summary>
    [Fact]
    public async Task A_host_already_draining_gets_the_deadline_arrival_plus_budget_even_when_the_suspend_waited()
    {
        var h = await MountedAsync(Config(HostFixtures.OnDemand("archive", drive: "Q:")), "archive");
        h.RcloneFake.ConfirmUnmountAfterCall("Q:", int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("archive")); // t0: drain begins, call #1
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.RcloneFake.UnmountCallCountFor("Q:"));

        var suspend = h.Supervisor.SuspendAsync(); // arrival t0+1, deadline t0+4
        h.FireTimers(TimeSpan.FromSeconds(Budget)); // t0+4: the suspend waited the whole budget; the 5 s retry is not due
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(3, h.RcloneFake.UnmountCallCountFor("Q:")); // #2 re-issue on suspend, #3 forced, both now
    }

    // ------------------------------------------------------------------------------------------
    // bs-4ca: a cancelled probe at shutdown is not a failure
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> bs-4ca. At shutdown <c>HostProbe</c> returns <c>Cancelled</c> instead of
    /// throwing, and the mounted handlers counted every non-Success outcome as a failure, so a host one
    /// failure short of its threshold drained as Automatic (a spurious toast) while Bosun was exiting.
    /// The token decides, not the outcome: the supervisor's own token being cancelled is shutdown.
    /// <b>Catches:</b> reading the outcome, for the shallow and the deep probe alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_probe_cancelled_by_shutdown_is_not_a_failure_and_drains_nothing(bool deep)
    {
        var h = await MountedAsync(Config(failuresBeforeUnmount: 1, HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:")));
        if (deep)
        {
            // One deep failure already counted: a second, even a cancelled one, would reach the threshold of 2.
            h.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed);
            await h.AdvanceAsync(TimeSpan.FromSeconds(300));
            Assert.Equal(1, h.Snapshot("prod").ConsecutiveDeepProbeFailures);
        }

        var held = deep
            ? h.Gate.HoldNextDeep("prod", HoldMode.ReturnCancelled)
            : h.Gate.HoldNextShallow(Hostname("prod"), HoldMode.ReturnCancelled);
        var warningsBefore = h.Log.Entries.Count(e => e.Level >= LogLevel.Warning);

        using var shutdown = new CancellationTokenSource();
        h.FireTimers(TimeSpan.FromSeconds(deep ? 300 : 60));
        var pump = h.Supervisor.DrainAsync(shutdown.Token);
        await held.Entered.WaitAsync(PreemptionHarness.Guard);

        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(PreemptionHarness.Guard));
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Empty(h.DrainCauses("prod"));
        Assert.Equal(warningsBefore, h.Log.Entries.Count(e => e.Level >= LogLevel.Warning)); // no "probe failed N/N" line
    }
}
