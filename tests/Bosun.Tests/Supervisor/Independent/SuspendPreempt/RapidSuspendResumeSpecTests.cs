using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// ADR-023 decision 1's reason for a counter rather than a flag ("because suspend, resume, suspend can be
/// pending together") and the rejected priority lane ("the second suspend would run before the resume it
/// follows"), attacked with every call issued while something holds the channel. Written from the ADR,
/// not from the implementation.
/// </summary>
public sealed class RapidSuspendResumeSpecTests
{
    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));

    /// <summary>
    /// <b>Protects:</b> the counter: after suspend #1 is dequeued, suspend #2 is still pending, so the resume
    /// between them must not mount; and channel order: the machine ends suspended, not resumed.
    /// <b>Catches:</b> a pending FLAG (suspend #1's dequeue clears it while #2 is still queued, so the resume
    /// remounts the drive seconds before the second sleep); a priority lane (the final state would be
    /// "resumed" with the drive mounted while the machine sleeps).
    /// </summary>
    [Fact]
    public async Task Suspend_resume_suspend_issued_behind_a_hung_tick_ends_suspended_with_nothing_mounted()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"));
        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the mounted shallow probe");

        var s1 = SuspendCaller.Call(h.Supervisor);
        var r = h.Supervisor.ResumeAsync();
        var s2 = SuspendCaller.Call(h.Supervisor);
        await h.FinishAsync(pump, "the suspend waited for the hung tick", s1, r, s2);

        Assert.Equal(1, h.MountCalls("A:"));
        Assert.False(h.IsMountedInRclone("A:"));
        Assert.NotEqual(MountState.Mounted, h.State("alpha"));

        // Still suspended: nothing comes back on its own.
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(1, h.MountCalls("A:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(2, h.MountCalls("A:"));
    }

    /// <summary>
    /// <b>Protects:</b> the counter in the other direction: resume #1 runs while suspend #2 is pending (no
    /// mount), then suspend #2, then resume #2 mounts exactly once.
    /// <b>Catches:</b> resume #1 ignoring the pending suspend -- the drive is mounted, drained by suspend #2,
    /// and mounted again (two extra rc round-trips and a drive created just before a sleep).
    /// </summary>
    [Fact]
    public async Task Resume_suspend_resume_issued_behind_a_hung_resume_probe_mounts_exactly_once_at_the_end()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State("alpha"));

        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"), OnCancel.Ignore);
        var r1 = h.Supervisor.ResumeAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "resume #1's probe of the drained host");

        var s2 = SuspendCaller.Call(h.Supervisor);
        var r2 = h.Supervisor.ResumeAsync();

        // Checked while the probe is still held: see Hold.CancellationRequested (bs-s30).
        Assert.False(hold.CancellationRequested, "a resume (a command) had its probe preempted by the suspend");
        hold.Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "resume #1 never finished", r1, s2, r2);

        Assert.False(hold.TokenCancelled, "a resume (a command) was preempted by the suspend");
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(2, h.MountCalls("A:"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
    }

    /// <summary>
    /// <b>Protects:</b> the counter's balance. Windows can deliver the suspend notification twice; both are
    /// pending together and both are dequeued, so the gate must reopen after the one resume.
    /// <b>Catches:</b> an increment per call with a single decrement (or a decrement skipped for a suspend that
    /// found nothing to do): the gate stays closed forever and no drive ever mounts again until Bosun restarts.
    /// </summary>
    [Fact]
    public async Task Two_suspends_behind_a_hung_tick_and_one_resume_leave_the_gate_open()
    {
        var h = NewHarness(
            HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"),
            HostFixtures.OnDemand("od", drive: "Q:"));
        await h.StartAsync();
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"));
        h.FireTimers(TimeSpan.FromSeconds(60));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the mounted shallow probe");

        var s1 = SuspendCaller.Call(h.Supervisor);
        var s2 = SuspendCaller.Call(h.Supervisor);
        await h.FinishAsync(pump, "the suspend waited for the hung tick", s1, s2);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));

        // And a user's click is no longer refused as "suspended".
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        Assert.Equal(MountState.Mounted, h.State("od"));
    }
}
