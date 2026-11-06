using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// ADR-023 decisions 2 and 3, against Invariant I2: a suspend cancels the recurring check that is running,
/// and that cancellation is not a failed probe. Written from ADR-023 and docs/ARCHITECTURE.md §4 rule 5
/// ("A suspend preempts the routine checks that are running"), not from the implementation.
/// </summary>
/// <remarks>
/// The trap ADR-023's context names: <c>HostProbe</c> reports a cancelled probe as the outcome
/// <c>Cancelled</c>, and the mounted handlers count every non-<c>Success</c> outcome as a failure. A host one
/// failure short of its threshold would then drain as <c>Automatic</c> -- with a toast saying the host
/// failed -- every time the machine sleeps. Each test here starts from exactly that host.
/// </remarks>
public sealed class PreemptedTickIsNotAFailureSpecTests
{
    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));

    /// <summary>Mounted at t=0, then two scripted shallow failures at t=60 and t=120.</summary>
    private static async Task<PreemptSpecHarness> MountedTwoShallowFailuresShortAsync()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));

        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("alpha"), ShallowProbeOutcome.ConnectionRefused, times: 2);
        await h.AdvanceSecondsAsync(120);
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(2, h.Snapshot("alpha").ConsecutiveMountedFailures);
        return h;
    }

    /// <summary>
    /// <b>Protects:</b> ADR-023 decision 3 ("decided by the token, never by the outcome or the exception
    /// type ... No counter ... changes") with Invariant I2's threshold one short.
    /// <b>Catches:</b> (LikeProduction) the context's trap -- the preempted probe's <c>Cancelled</c> outcome
    /// counted as the third failure, so the suspend becomes an <c>Automatic</c> drain. (Throw) a wrapper whose
    /// catch filter lets the preemption's <see cref="OperationCanceledException"/> be treated as a failure
    /// or escape the tick. (Ignore) a check on the OUTCOME rather than the token: here the probe returns a
    /// genuine-looking <c>ConnectionRefused</c> after its token was cancelled, and the ADR says that is
    /// discarded too.
    /// </summary>
    [Theory]
    [InlineData(OnCancel.LikeProduction)]
    [InlineData(OnCancel.Throw)]
    [InlineData(OnCancel.Ignore)]
    public async Task A_host_one_shallow_failure_short_drains_as_Suspend_when_its_probe_is_preempted(OnCancel onCancel)
    {
        var h = await MountedTwoShallowFailuresShortAsync();
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"), onCancel);

        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the third mounted shallow probe");

        var suspend = SuspendCaller.Call(h.Supervisor);
        if (onCancel == OnCancel.Ignore)
        {
            // The probe returns a real failure, but only after the suspend cancelled its token.
            await PreemptSpecHarness.WaitForAsync(() => hold.TokenCancelled, "the suspend never cancelled the running tick's token");
            hold.Release(ShallowProbeOutcome.ConnectionRefused);
        }

        await h.FinishAsync(pump, "the suspend waited for the hung shallow probe", suspend);
        await suspend;

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.False(h.IsMountedInRclone("A:"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the same, for the mounted deep probe (ADR-023 decision 2 lists it; §4 rule 3a's own
    /// counter drains at a fixed 2).
    /// <b>Catches:</b> the deep handler counting the preempted <c>Cancelled</c> as its second failure.
    /// </summary>
    [Fact]
    public async Task A_host_one_deep_failure_short_drains_as_Suspend_when_its_deep_probe_is_preempted()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        h.Probe.SetDeep("alpha", DeepProbeOutcome.Failed, times: 1);
        await h.AdvanceSecondsAsync(300);
        Assert.Equal(1, h.Snapshot("alpha").ConsecutiveDeepProbeFailures);
        Assert.Equal(MountState.Mounted, h.State("alpha"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(299));
        var hold = h.HProbe.HoldNextDeep("alpha");
        h.FireTimers(TimeSpan.FromSeconds(1));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the second mounted deep probe");

        var suspend = SuspendCaller.Call(h.Supervisor);
        await h.FinishAsync(pump, "the suspend waited for the hung deep probe", suspend);
        await suspend;

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
        Assert.False(h.IsMountedInRclone("A:"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> I2 survives the change: a host whose probes genuinely failed BEFORE the suspend is
    /// already <c>Draining</c> as <c>Automatic</c>; rule 5 says it keeps that cause, and the suspend must
    /// still get the drive off the machine.
    /// <b>Catches:</b> a suspend that, in skipping "preempted" work, also skips or overwrites a drain that
    /// was really caused by failures, or leaves the drive mounted because the first unmount did not confirm.
    /// </summary>
    [Fact]
    public async Task A_host_that_genuinely_failed_before_the_suspend_keeps_its_Automatic_drain_and_is_unmounted()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        h.Rclone.ConfirmUnmountAtCall("A:", 2); // the drain's first unmount does not take
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("alpha"), ShallowProbeOutcome.ConnectionRefused, times: 3);
        await h.AdvanceSecondsAsync(180);
        Assert.Equal(MountState.Draining, h.State("alpha"));

        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(5);

        Assert.Equal([DrainCause.Automatic], h.DrainCauses("alpha"));
        Assert.False(h.IsMountedInRclone("A:"), "a host that failed before the suspend was left mounted through it");
        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.Equal(1, h.TotalMountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> ADR-023 decision 2, last sentence: "A tick that starts while a suspend is pending
    /// sees the counter, cancels itself and skips its I/O, so ticks queued ahead of the suspend do no work
    /// either."
    /// <b>Catches:</b> preempting only the running tick, so every tick already queued behind it still runs
    /// its probe (and VFS sample) before the suspend -- the "about 30 s" case in ADR-023's context. Also
    /// catches the queued tick counting a skipped probe as a failure.
    /// </summary>
    [Fact]
    public async Task A_tick_queued_behind_the_preempted_one_skips_its_probe()
    {
        var h = NewHarness(
            HostFixtures.Persistent("alpha", probeIntervalSeconds: 30, drive: "A:"),
            HostFixtures.Persistent("bravo", probeIntervalSeconds: 45, drive: "B:"));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("bravo"), ShallowProbeOutcome.ConnectionRefused);
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"));

        h.FireTimers(TimeSpan.FromSeconds(45)); // alpha's tick at 30 s, then bravo's at 45 s
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "alpha's shallow probe at t=30");
        var bravoProbesBefore = h.ShallowCalls("bravo");

        var suspend = SuspendCaller.Call(h.Supervisor);
        await h.FinishAsync(pump, "the suspend waited for the hung shallow probe", suspend);
        await suspend;

        Assert.Equal(bravoProbesBefore, h.ShallowCalls("bravo"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("bravo"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> ADR-023 decision 2 and §5: the prologue runs on the caller's thread, "never blocks",
    /// and uses <c>CancelAsync</c>, never <c>Cancel</c>, so the callbacks that release a hung call do not run
    /// on <c>SystemEvents</c>' notification thread.
    /// <b>Catches:</b> <c>Cancel()</c> in the prologue (the held call's cancellation callback would run inline
    /// inside the <c>SuspendAsync</c> call), and a prologue that waits for the running tick.
    /// </summary>
    [Fact]
    public async Task The_prologue_cancels_the_running_tick_without_running_its_callbacks_on_the_callers_thread()
    {
        var h = await MountedTwoShallowFailuresShortAsync();
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"));
        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the mounted shallow probe");

        var call = Task.Factory.StartNew(
            () => SuspendCaller.Call(h.Supervisor), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        Task suspend;
        try
        {
            suspend = await call.WaitAsync(PreemptSpecHarness.Guard);
        }
        catch (TimeoutException)
        {
            Assert.Fail("SuspendAsync blocked its caller's thread (SystemEvents' thread in production).");
            return;
        }

        await h.FinishAsync(pump, "the suspend waited for the hung shallow probe", suspend);

        Assert.True(hold.TokenCancelled, "the suspend did not cancel the running tick at all");
        Assert.False(
            hold.CancelCallbackRanInsideSuspendCall,
            "the running tick's cancellation callbacks ran inline on the thread that called SuspendAsync (Cancel, not CancelAsync)");
    }
}
