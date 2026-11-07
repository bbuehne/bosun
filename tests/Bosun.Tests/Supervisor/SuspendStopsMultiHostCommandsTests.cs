using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// ADR-023 amendment (bs-5ga.1, maintainer decision B of 2026-11-06): a command is still not preempted,
/// so the call it has in flight finishes, but a command that works through several hosts checks for a
/// pending suspend BEFORE it starts the next one and stops. A suspend therefore waits behind at most one
/// call, not one per remaining host (about 5 s each, against Windows' roughly 2 s).
/// </summary>
/// <remarks>
/// The shape of every test: three hosts, a command running over them with one call held open, a suspend
/// arriving while it is held, the call released, and then a look at which calls were NEVER issued. A
/// skipped host is checked three ways: no probe was issued for it, nothing about it changed (state, drain
/// cause, backoff, timer), and the resume that follows the suspend does reach it. Nothing is cancelled by
/// the suspend, which <see cref="TokenLineage"/> shows (not the token's own flag, which a link only
/// receives when the thread pool runs it). Fakes and the fake clock only; no real rclone, mount, host,
/// drive letter or wt.exe.
/// </remarks>
public sealed class SuspendStopsMultiHostCommandsTests
{
    private static readonly string[] Keys = ["a", "b", "c"];

    private static DrainCause[] Causes(params DrainCause[] causes) => causes;

    private static BosunConfig Config(params HostConfig[] hosts) =>
        // failures_before_unmount = 1: any probe wrongly counted as a failure would drain the host as
        // Automatic, which the cause assertions below would show.
        HostFixtures.Build(HostFixtures.Global(failuresBeforeUnmount: 1, suspendUnmountTimeoutSeconds: 3), hosts);

    private static HostConfig[] ThreePersistent() =>
    [
        HostFixtures.Persistent("a", probeIntervalSeconds: 60, drive: "A:"),
        HostFixtures.Persistent("b", probeIntervalSeconds: 60, drive: "B:"),
        HostFixtures.Persistent("c", probeIntervalSeconds: 60, drive: "C:"),
    ];

    /// <summary>Three persistent hosts, started and mounted.</summary>
    private static async Task<PreemptionHarness> ThreeMountedAsync()
    {
        var h = new PreemptionHarness(Config(ThreePersistent()));
        await h.StartAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        return h;
    }

    private static int Shallow(PreemptionHarness h, string key) =>
        h.Probe.ShallowProbeCalls.Count(hostname => hostname == PreemptionHarness.Hostname(key));

    private static int Deep(PreemptionHarness h, string key) =>
        h.Probe.DeepProbeCalls.Count(hostKey => hostKey == key);

    /// <summary>Calls <c>SuspendAsync</c> while <paramref name="pump"/> is running a command held open on
    /// <paramref name="held"/>, checks the suspend did not cancel the held call, releases it, and runs
    /// everything to completion.</summary>
    private static async Task SuspendWhileHeldAsync(
        PreemptionHarness h, Task pump, Task command, HeldCall held, string what)
    {
        var suspend = h.Supervisor.SuspendAsync();
        // TokenLineage, not the token's own flag: see SuspendPreemptionTests (bs-s30).
        Assert.False(TokenLineage.CancellationRequested(held.Token), $"a suspend must not cancel {what}");
        held.Release();
        await pump.WaitAsync(PreemptionHarness.Guard);
        await h.Pump();
        await suspend.WaitAsync(PreemptionHarness.Guard);
        await command.WaitAsync(PreemptionHarness.Guard);
    }

    // ------------------------------------------------------------------------------------------
    // 1. The re-derivation shared by resume and network change
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the whole point of bs-5ga.1 for the re-derivation (ADR-017): a suspend that arrives
    /// while ANY of its calls is in flight waits for that one call, and none of the calls after it is
    /// started. Per row: the call held is <c>core/version</c>, <c>mount/listmounts</c>, host a's forced
    /// shallow probe, or host a's forced deep probe. Hosts b and c are never probed in any row, and host a
    /// gets no deep probe when only its shallow probe was in flight.
    /// <b>Catches:</b> a loop with no check (b and c are probed), a check only before each host (a's deep
    /// probe still runs after its shallow one), a check missing before <c>mount/listmounts</c>, and a
    /// suspend that cancels the call in flight. The drains must all be <c>Suspend</c>: a skipped host
    /// counted as a failure would be <c>Automatic</c> here (failures_before_unmount is 1).
    /// </summary>
    [Theory]
    [InlineData(false, "core/version")]
    [InlineData(false, "listmounts")]
    [InlineData(false, "shallow")]
    [InlineData(false, "deep")]
    [InlineData(true, "core/version")]
    [InlineData(true, "listmounts")]
    [InlineData(true, "shallow")]
    [InlineData(true, "deep")]
    public async Task A_suspend_arriving_during_a_re_derivation_waits_for_the_call_in_flight_and_no_later_host_is_probed(
        bool networkChange, string inFlight)
    {
        var h = await ThreeMountedAsync();
        var held = inFlight switch
        {
            "core/version" => h.Rclone.HoldNextGetVersion(),
            "listmounts" => h.Rclone.HoldNextListMounts(),
            "shallow" => h.Gate.HoldNextShallow(PreemptionHarness.Hostname("a"), HoldMode.IgnoreToken),
            _ => h.Gate.HoldNextDeep("a", HoldMode.IgnoreToken),
        };
        var version = h.RcloneFake.GetVersionCallCount;
        var listMounts = h.RcloneFake.ListMountsCallCount;
        var shallow = Keys.ToDictionary(k => k, k => Shallow(h, k));
        var deep = Keys.ToDictionary(k => k, k => Deep(h, k));

        var command = networkChange ? h.Supervisor.NetworkChangedAsync() : h.Supervisor.ResumeAsync();
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await SuspendWhileHeldAsync(h, pump, command, held, $"the in-flight {inFlight} call");

        // Exactly the calls up to and including the one in flight, and none after it.
        Assert.Equal(version + 1, h.RcloneFake.GetVersionCallCount); // the one in flight, or the one before it
        Assert.Equal(
            listMounts + (inFlight == "core/version" ? 0 : 1) + 1, // the re-derivation's own, then the suspend drain's verify
            h.RcloneFake.ListMountsCallCount);
        // A held probe never reaches the fake that records calls (held.Entered showed it was issued), so
        // the recorded counts are: a's shallow probe only in the row where it completed (before the held
        // deep one), and a's deep probe never.
        var aShallow = inFlight == "deep" ? 1 : 0;
        var aDeep = 0;
        Assert.Equal(shallow["a"] + aShallow, Shallow(h, "a"));
        Assert.Equal(deep["a"] + aDeep, Deep(h, "a"));
        Assert.Equal(shallow["b"], Shallow(h, "b"));
        Assert.Equal(shallow["c"], Shallow(h, "c"));
        Assert.Equal(deep["b"], Deep(h, "b"));
        Assert.Equal(deep["c"], Deep(h, "c"));

        // The suspend drained everything, as a suspend. Nothing was marked failed.
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Disabled, h.State(key));
            Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses(key));
        }

        Assert.Contains(h.Log.Entries, e => e.Message.Contains("a suspend is pending", StringComparison.Ordinal)
            && e.Message.Contains("bs-5ga.1", StringComparison.Ordinal));

        // The resume that follows reaches every host, the skipped ones included.
        await h.ResumeAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        Assert.True(Shallow(h, "b") > shallow["b"], "the resume after the suspend must probe b");
        Assert.True(Shallow(h, "c") > shallow["c"], "the resume after the suspend must probe c");
    }

    /// <summary>
    /// <b>Protects:</b> the check at the top of the re-derivation. A suspend that is already pending when a
    /// resume or network change STARTS means nothing is in flight, so nothing at all is started: not
    /// <c>core/version</c>, not <c>mount/listmounts</c>, no probe. Suspend, resume, suspend queued back to
    /// back is the real-world shape.
    /// <b>Catches:</b> a re-derivation whose first check comes only after its first call.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_that_starts_with_a_suspend_already_pending_starts_no_call(bool networkChange)
    {
        var h = await ThreeMountedAsync();
        var version = h.RcloneFake.GetVersionCallCount;
        var listMounts = h.RcloneFake.ListMountsCallCount;
        var shallow = h.Probe.ShallowProbeCalls.Count;
        var deep = h.Probe.DeepProbeCalls.Count;

        var command = networkChange ? h.Supervisor.NetworkChangedAsync() : h.Supervisor.ResumeAsync();
        var suspend = h.Supervisor.SuspendAsync();
        await h.Pump();
        await command.WaitAsync(PreemptionHarness.Guard);
        await suspend.WaitAsync(PreemptionHarness.Guard);

        Assert.Equal(version, h.RcloneFake.GetVersionCallCount);
        Assert.Equal(listMounts + 1, h.RcloneFake.ListMountsCallCount); // the suspend drain's verification only
        Assert.Equal(shallow, h.Probe.ShallowProbeCalls.Count);
        Assert.Equal(deep, h.Probe.DeepProbeCalls.Count);
        foreach (var key in Keys)
        {
            Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses(key));
        }

        await h.ResumeAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }
    }

    // ------------------------------------------------------------------------------------------
    // 2. The per-host loops of resume and network change
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the recovery of <c>Disabled</c> hosts that resume's loop did not reach. Host a's
    /// enable probe is in flight when the second suspend arrives; b and c are not started, and stay
    /// <c>Disabled</c> with no probe. The resume after that second suspend enables them.
    /// <b>Note:</b> this does NOT pin the loop's check. <c>EnableHostAsync</c> already returns before
    /// <c>Probing</c> while a suspend is pending (ADR-023 decision 1), so a <c>Disabled</c> host is safe
    /// without it; the next test is the one that fails without the check, because a forced probe of a
    /// <c>Ready</c> or <c>Unreachable</c> host has no such gate.
    /// </summary>
    [Fact]
    public async Task Disabled_hosts_a_resume_did_not_reach_are_enabled_by_the_next_resume()
    {
        var h = await ThreeMountedAsync();
        await h.SuspendAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Disabled, h.State(key));
        }

        var shallow = Keys.ToDictionary(k => k, k => Shallow(h, k));
        var mounts = h.RcloneFake.MountCalls.Count;
        var held = h.Gate.HoldNextShallow(PreemptionHarness.Hostname("a"), HoldMode.IgnoreToken);

        var resume = h.Supervisor.ResumeAsync();
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await SuspendWhileHeldAsync(h, pump, resume, held, "host a's enable probe");

        Assert.Equal(shallow["a"], Shallow(h, "a")); // held, so not recorded: held.Entered showed it was issued
        Assert.Equal(shallow["b"], Shallow(h, "b"));
        Assert.Equal(shallow["c"], Shallow(h, "c"));
        Assert.Equal(MountState.Disabled, h.State("b"));
        Assert.Equal(MountState.Disabled, h.State("c"));
        Assert.Equal(mounts, h.RcloneFake.MountCalls.Count); // a is Ready, held back by the pending suspend

        await h.ResumeAsync();

        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        Assert.Equal(shallow["b"] + 1, Shallow(h, "b"));
        Assert.Equal(shallow["c"] + 1, Shallow(h, "c"));
    }

    /// <summary>
    /// <b>Protects:</b> the loops of network change and of resume over <c>Ready</c> and <c>Unreachable</c>
    /// hosts, and what a skipped host is left as: untouched. Hosts a, b and c are all dark. Host a's forced
    /// probe is in flight when the suspend arrives; b and c are not probed, their backoff is not reset (a
    /// reset before the check would zero it) and their idle timers are still armed (they fire during the
    /// suspend and skip, D5), so nothing is left without a timer. The resume that follows probes and
    /// mounts them.
    /// <b>Catches:</b> a loop with no check, a check placed after the backoff reset, and one placed after
    /// the timer was disposed (the forced-probe helper disposes it first).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_change_and_resume_stop_before_the_next_host_and_leave_a_skipped_host_exactly_as_it_was(
        bool resume)
    {
        var h = new PreemptionHarness(Config(ThreePersistent()));
        foreach (var key in Keys)
        {
            h.Probe.EnqueueShallow(PreemptionHarness.Hostname(key), Bosun.Probe.ShallowProbeOutcome.ConnectionRefused);
        }

        await h.StartAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Unreachable, h.State(key));
        }

        var shallow = Keys.ToDictionary(k => k, k => Shallow(h, k));
        var failures = Keys.ToDictionary(k => k, k => h.Snapshot(k).ConsecutiveIdleFailures);
        Assert.All(failures.Values, f => Assert.True(f > 0, "the backoff must be non-zero for this test to mean anything"));
        var held = h.Gate.HoldNextShallow(PreemptionHarness.Hostname("a"), HoldMode.IgnoreToken);

        var command = resume ? h.Supervisor.ResumeAsync() : h.Supervisor.NetworkChangedAsync();
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await SuspendWhileHeldAsync(h, pump, command, held, "host a's forced probe");

        Assert.Equal(shallow["a"], Shallow(h, "a")); // held, so not recorded: held.Entered showed it was issued
        Assert.Equal(shallow["b"], Shallow(h, "b"));
        Assert.Equal(shallow["c"], Shallow(h, "c"));
        Assert.Equal(MountState.Unreachable, h.State("b"));
        Assert.Equal(MountState.Unreachable, h.State("c"));
        Assert.Equal(failures["b"], h.Snapshot("b").ConsecutiveIdleFailures);
        Assert.Equal(failures["c"], h.Snapshot("c").ConsecutiveIdleFailures);
        Assert.DoesNotContain(h.RcloneFake.MountCalls, m => m.MountPoint is "A:" or "B:" or "C:");

        // Still on their own timers: with the system suspended each tick skips and re-arms.
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.True(h.InformationLines("Idle probe tick for b skipped") >= 1, "b's timer was taken away");
        Assert.True(h.InformationLines("Idle probe tick for c skipped") >= 1, "c's timer was taken away");
        Assert.Equal(shallow["b"], Shallow(h, "b"));
        Assert.Equal(shallow["c"], Shallow(h, "c"));

        await h.ResumeAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        Assert.True(Shallow(h, "b") > shallow["b"] && Shallow(h, "c") > shallow["c"]);
    }

    // ------------------------------------------------------------------------------------------
    // 3. The deferred-remount sweep
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the sweep (ADR-022 amendment) that re-probes every on-demand host holding a remount
    /// record once a gate clears. Host a's re-probe is in flight when a suspend arrives; b is not started
    /// and KEEPS its record, so the resume after the suspend still brings both drives back.
    /// <b>Catches:</b> a sweep loop with no check, and a skip that drops the record.
    /// </summary>
    [Fact]
    public async Task The_deferred_remount_sweep_stops_before_the_next_host_and_keeps_its_remount_record()
    {
        var h = new PreemptionHarness(Config(
            HostFixtures.OnDemand("a", drive: "A:"),
            HostFixtures.OnDemand("b", drive: "B:")));
        await h.StartAsync();
        foreach (var key in new[] { "a", "b" })
        {
            await h.RunAsync(() => h.Supervisor.RequestMountAsync(key));
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.WinFspMissing, "WinFsp is not installed")));
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State("a")); // the gate keeps both records
        Assert.Equal(MountState.Ready, h.State("b"));
        var shallow = new[] { "a", "b" }.ToDictionary(k => k, k => Shallow(h, k));
        var held = h.Gate.HoldNextShallow(PreemptionHarness.Hostname("a"), HoldMode.IgnoreToken);

        var reopen = h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available);
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await SuspendWhileHeldAsync(h, pump, reopen, held, "host a's sweep probe");

        Assert.Equal(shallow["a"], Shallow(h, "a")); // held, so not recorded: held.Entered showed it was issued
        Assert.Equal(shallow["b"], Shallow(h, "b"));
        Assert.Equal(MountState.Ready, h.State("a")); // no mount: the suspend is pending
        Assert.Equal(MountState.Ready, h.State("b"));
        Assert.Equal(2, h.RcloneFake.MountCalls.Count); // only the original two

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("a"));
        Assert.Equal(MountState.Mounted, h.State("b")); // b's record survived the skipped sweep
        Assert.Equal(4, h.RcloneFake.MountCalls.Count);
    }

    // ------------------------------------------------------------------------------------------
    // 4. "Unmount all & re-probe"
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the repair command's re-probe loop (its second step). Nothing is mounted and all
    /// three hosts are dark; host a's forced probe is in flight when the suspend arrives, and b and c are
    /// neither probed nor have their backoff reset. The resume after the suspend mounts all three.
    /// <b>Catches:</b> a re-probe loop with no check.
    /// </summary>
    [Fact]
    public async Task Repair_re_probe_step_stops_before_the_next_host_when_a_suspend_arrives()
    {
        var h = new PreemptionHarness(Config(ThreePersistent()));
        foreach (var key in Keys)
        {
            h.Probe.EnqueueShallow(PreemptionHarness.Hostname(key), Bosun.Probe.ShallowProbeOutcome.ConnectionRefused);
        }

        await h.StartAsync();
        var shallow = Keys.ToDictionary(k => k, k => Shallow(h, k));
        var failures = Keys.ToDictionary(k => k, k => h.Snapshot(k).ConsecutiveIdleFailures);
        var held = h.Gate.HoldNextShallow(PreemptionHarness.Hostname("a"), HoldMode.IgnoreToken);

        var repair = h.Supervisor.RepairAllAsync(); // nothing is mounted: straight to the re-probe step
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await SuspendWhileHeldAsync(h, pump, repair, held, "host a's repair re-probe");

        Assert.Equal(shallow["b"], Shallow(h, "b"));
        Assert.Equal(shallow["c"], Shallow(h, "c"));
        Assert.Equal(failures["b"], h.Snapshot("b").ConsecutiveIdleFailures);
        Assert.Equal(failures["c"], h.Snapshot("c").ConsecutiveIdleFailures);

        await h.ResumeAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> the repair command's drain loop. Host a's <c>mount/unmount</c> is in flight when a
    /// suspend arrives; it finishes (not cancelled), b and c are not drained by the repair and the suspend
    /// drains them instead, as a suspend; the repair's re-probe step does not start either.
    /// <b>Catches:</b> a repair loop with no check (b and c would be drained with cause Repair, each a
    /// full unmount and verify the suspend waits behind).
    /// </summary>
    [Fact]
    public async Task Repair_stops_draining_hosts_when_a_suspend_arrives_and_the_suspend_drains_the_rest()
    {
        var h = await ThreeMountedAsync();
        var held = h.Rclone.HoldNextUnmount();

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.Pump();
        await held.Entered.WaitAsync(PreemptionHarness.Guard);
        await SuspendWhileHeldAsync(h, pump, repair, held, "host a's repair unmount");

        Assert.Equal(Causes(DrainCause.Repair), h.DrainCauses("a"));
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("b"));
        Assert.Equal(Causes(DrainCause.Suspend), h.DrainCauses("c"));
        Assert.All(h.Rclone.UnmountTokens, token => Assert.False(TokenLineage.CancellationRequested(token)));
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Disabled, h.State(key));
        }

        await h.ResumeAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }
    }
}
