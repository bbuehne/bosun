using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// The resume side of Invariant I8, aimed at the failure mode that actually occurs on the
/// maintainer's machine: a desktop that sleeps overnight and wakes the next morning. The docking /
/// changed-network story in docs/OPERATIONS.md T2 is covered elsewhere and does not apply to this
/// hardware -- what applies is a long sleep, and a network that may have been interrupted while the
/// machine was down.
/// </summary>
/// <remarks>
/// <para>
/// Two spec sources drive every expectation here, both read as written:
/// docs/ARCHITECTURE.md §3's <c>ISystemEventSource</c> table (<c>Resume</c> → "Re-probe all
/// immediately, bypassing backoff") and §6's failure-mode row ("Machine sleeps with mounts up |
/// Unmount on suspend. <b>If we missed it, force-unmount and reconcile on resume.</b>"). §3's
/// suspend paragraph makes the same point from the other end: "accept that a forced unmount may be
/// needed on resume."
/// </para>
/// <para>
/// A real sleep freezes the machine: no timer fires while it is down. That is modelled by advancing
/// the fake clock <b>not at all</b> between suspend and resume -- which also makes "immediately" a
/// strictly stronger assertion than any elapsed-time one could be.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock. Nothing reaches WinFsp, a
/// real drive letter, a real host, or a real system event.
/// </para>
/// </remarks>
public sealed class ResumeAfterOvernightSleepTests
{
    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §4 Backoff -- "reset to zero by ... a power resume" --
    /// with the ordering that makes the reset mean anything: it has to happen <i>before</i> the probe
    /// resume forces, not after.
    /// <b>Catches:</b> resetting the ladder after the forced probe, or resetting the counter without
    /// re-arming the timer. Both leave the host's next retry scheduled on the rung it had reached
    /// before the sleep -- five minutes on the default ladder. The symptom is the one the Backoff
    /// section names: waking the machine and watching a drive take minutes to return for no visible
    /// reason. The equivalent test for a network address change already exists; this is the resume
    /// branch, which is a separate <c>switch</c> case and can regress on its own.
    /// </summary>
    [Fact]
    public async Task Resume_resets_the_backoff_ladder_before_the_probe_it_forces()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(
            HostFixtures.Build(HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]), host));
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;

        await harness.StartAsync();
        await harness.AdvanceSecondsAsync(5);
        await harness.AdvanceSecondsAsync(15);
        await harness.AdvanceSecondsAsync(30);

        Assert.Equal(MountState.Unreachable, harness.State("prod"));
        Assert.Equal(4, harness.Snapshot("prod").ConsecutiveIdleFailures); // next rung would be 60s

        // Overnight sleep. No clock movement: a sleeping machine fires no timers.
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        // The host is still down in the morning, so the probe resume forced has failed -- and the
        // ladder it failed onto must be rung 1, not rung 5.
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveIdleFailures);

        var probes = harness.ShallowProbesFor("prod");
        await harness.AdvanceSecondsAsync(4);
        Assert.Equal(probes, harness.ShallowProbesFor("prod"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(probes + 1, harness.ShallowProbesFor("prod"));
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §3 -- <c>Resume</c> → "Re-probe <b>all</b> immediately".
    /// "All" includes a host still believed <c>Mounted</c>, which is precisely the host §6's "if we
    /// missed it" row is about.
    /// <b>Catches:</b> a resume path that assumes suspend always ran and always finished, and so
    /// skips <c>Mounted</c> hosts entirely. A mount that has been asleep for eight hours has a dead
    /// SSH connection underneath it whatever the host's reachability now is, so the first check of
    /// it should be the resume itself. Without that, the first check is whenever the mounted-probe
    /// timer armed <i>before</i> the sleep happens to fire -- a moment whose relationship to the wake
    /// depends on how the platform timer treats suspended time, which is exactly the uncertainty §3's
    /// "immediately" exists to remove. Every second of that uncertainty is a second in which the
    /// user's Explorer can be the thing that discovers the dead mount, which is
    /// docs/OPERATIONS.md's first triage row.
    /// </summary>
    /// <remarks>
    /// Bosun genuinely reaches this state. Windows does not guarantee a <c>PowerModeChanged(Suspend)</c>
    /// notification -- it is skipped on some fast-startup and modern-standby paths, it is not
    /// delivered to a process that starts while the machine is already asleep, and the adapter itself
    /// abandons the drain when <c>global.suspend_unmount_timeout_seconds</c> elapses. §6 exists
    /// because the suspend-side handling is best effort.
    /// </remarks>
    [Fact]
    public async Task A_host_that_slept_still_Mounted_is_re_probed_immediately_on_resume()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        // The suspend notification never arrived: the machine slept with the mount up.
        var probesBefore = harness.ShallowProbesFor("prod");

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(probesBefore + 1, harness.ShallowProbesFor("prod"));
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §6 -- "Machine sleeps with mounts up ... if we missed
    /// it, force-unmount and <b>reconcile on resume</b>" -- and §3's "accept that a forced unmount
    /// may be needed on resume".
    /// <b>Catches:</b> a resume that never consults <c>mount/listmounts</c> at all, so the
    /// supervisor's belief about every drive letter after a sleep is whatever it was before the
    /// sleep. The state that survives a suspend is the least trustworthy state this application ever
    /// holds -- rclone's child process, WinFsp, and the network stack have all been through a power
    /// transition underneath it -- and §4's Reconciliation section exists for drift far less likely
    /// than this. The periodic reconciliation tick is not the same guarantee: it is a cadence, and
    /// the spec asks for reconciliation <i>on resume</i> precisely so the window between waking and
    /// the next tick is not a window in which Explorer can wedge.
    /// </summary>
    [Fact]
    public async Task Resume_reconciles_the_intended_state_against_listmounts()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        var listMountsBefore = harness.Rclone.ListMountsCalls;

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.True(
            harness.Rclone.ListMountsCalls > listMountsBefore,
            "Resume did not reconcile against mount/listmounts. docs/ARCHITECTURE.md §6 requires it " +
            "for exactly the case where the suspend-time unmount did not happen or did not confirm.");
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §4 rule 5 end to end for the case that actually happens
    /// -- sleep all night, wake up, the host is still there -- plus rule 6 ("on-demand hosts rest in
    /// <c>Ready</c>, not <c>Mounted</c>. They do not auto-mount").
    /// <b>Catches:</b> two opposite bugs with one setup. A resume that fails to re-enable and
    /// remount the persistent host leaves the maintainer with no drive letter after every overnight
    /// sleep -- the tool silently stops working once a day. A resume that re-mounts the on-demand
    /// host as well violates the tier split, and does it in the least visible way possible: an
    /// on-demand drive letter that reappears by itself the morning after a single manual mount, and
    /// keeps reappearing forever.
    /// </summary>
    [Fact]
    public async Task An_overnight_sleep_brings_back_the_persistent_drive_and_only_the_persistent_drive()
    {
        var persistent = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var onDemand = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), persistent, onDemand));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.Null(harness.Rclone.FsAt("P:"));
        Assert.Null(harness.Rclone.FsAt("Q:"));

        // Eight hours of sleep: the fake clock does not move, because neither does a sleeping
        // machine's. Resume alone must be enough.
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.NotNull(harness.Rclone.FsAt("P:"));

        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Null(harness.Rclone.FsAt("Q:"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I2 / ADR-005 across the resume boundary, for the host that comes
    /// back <i>unreachable</i> -- the overnight equivalent of a network interruption that has not
    /// healed by morning.
    /// <b>Catches:</b> a resume that treats "previously enabled" as "previously mounted, so mount it
    /// again", skipping the probe. Invariant I1 forbids that from any state, and after a sleep it is
    /// the single most dangerous shortcut available: a drive letter created immediately on wake,
    /// pointing at a host that is not answering, blocks Explorer process-wide across the OS.
    /// </summary>
    [Fact]
    public async Task A_host_that_is_still_down_in_the_morning_gets_no_drive_letter()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        // The switch is still rebooting when the machine wakes.
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(MountState.Unreachable, harness.State("prod"));
        Assert.Null(harness.Rclone.FsAt("P:"));
        Assert.Equal(1, harness.Rclone.MountCount("P:")); // only the original, pre-sleep mount

        // And it stays that way for as long as the host is down, however long that is.
        for (var i = 0; i < 20; i++)
        {
            await harness.AdvanceSecondsAsync(300);
            Assert.Null(harness.Rclone.FsAt("P:"));
        }

        // Then the switch comes back, and the drive returns on the ladder without any user action.
        harness.Probe.DefaultShallow = ShallowProbeOutcome.Success;
        await harness.AdvanceSecondsAsync(300);

        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.NotNull(harness.Rclone.FsAt("P:"));
    }
}
