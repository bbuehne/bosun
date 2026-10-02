using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// <see cref="IMountSupervisor.RepairAllAsync"/> (bs-aoz, "Unmount all and re-probe"), and the
/// reconciliation that "Restart rclone" relies on. The questions that matter: a repair drops
/// everything and starts clean (so it must not park anyone, ADR-015), a persistent host comes back
/// only through a fresh successful probe (I1), an on-demand host stays down, and nothing is called
/// unmounted until rclone says so (docs/ARCHITECTURE.md §4 rule 4).
/// </summary>
public sealed class RepairAllTests
{
    private static string Hostname(string key) => $"{key}.example.internal";

    // -- Persistent hosts: drain, then remount only after a fresh probe -------------------------

    [Fact]
    public async Task A_mounted_persistent_host_is_unmounted_re_probed_and_remounted()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        var shallowBefore = harness.Probe.ShallowProbeCalls.Count;
        var deepBefore = harness.Probe.DeepProbeCalls.Count;

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        Assert.Equal(["P:"], harness.Rclone.UnmountCalls);
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        // The remount was authorised by a probe that ran AFTER the repair began, shallow (the
        // re-enable) and deep (entering Mounting) -- not by the mount that was already up (I1).
        Assert.Equal(shallowBefore + 1, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal(deepBefore + 1, harness.Probe.DeepProbeCalls.Count);
    }

    [Fact]
    public async Task The_transition_sequence_for_a_repaired_persistent_host_goes_through_every_gate()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        var baseline = harness.Supervisor.GetTransitionHistory().Count;

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        var sequence = AfterBaseline(harness, baseline).Select(e => $"{e.From}->{e.To}").ToArray();
        Assert.Equal(
            [
                "Mounted->Draining",
                "Draining->Disabled",
                "Disabled->Probing",
                "Probing->Ready",
                "Ready->Mounting",
                "Mounting->Mounted",
            ],
            sequence);
    }

    [Fact]
    public async Task A_repaired_host_whose_fresh_probe_fails_is_not_remounted_until_a_later_probe_succeeds()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        harness.Probe.EnqueueShallow(Hostname("prod"), ShallowProbeOutcome.Timeout);

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        // Drained, and the re-probe failed: it is down and stays down. No second mount/mount.
        Assert.Equal(["P:"], harness.Rclone.UnmountCalls);
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        Assert.Single(harness.Rclone.MountCalls);

        // The persistent ladder polls it; the next probe passes, and only then does it mount.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
    }

    [Fact]
    public async Task A_repaired_host_whose_deep_probe_fails_is_not_mounted_and_is_paced()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, "ssh: handshake failed");

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        // Repair does not bypass the deep probe: the failed one routes to Draining, never Mounted.
        Assert.Single(harness.Rclone.MountCalls);
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveMountFailures);
    }

    // -- Parking (ADR-015) ----------------------------------------------------------------------

    [Fact]
    public async Task A_repair_does_not_park_anyone()
    {
        var persistent = HostFixtures.Persistent("prod", drive: "P:");
        var onDemand = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = Harness(persistent, onDemand);
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        Assert.False(harness.Snapshot("prod").UserParked);
        Assert.False(harness.Snapshot("archive").UserParked);

        // Proof that "not parked" is real, not only a flag: an automatic drain (three failed mounted
        // probes) is followed by an automatic remount. A parked host would stay down.
        for (var i = 0; i < 3; i++)
        {
            harness.Probe.EnqueueShallow(Hostname("prod"), ShallowProbeOutcome.Timeout);
        }

        for (var i = 0; i < 3; i++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        }

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(3, harness.Rclone.MountCalls.Count(m => m.MountPoint == "P:")); // start, repair, recovery
    }

    [Fact]
    public async Task A_host_the_user_parked_earlier_stays_parked_but_is_re_probed()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("prod"));
        Assert.True(harness.Snapshot("prod").UserParked);
        var mountCalls = harness.Rclone.MountCalls.Count;
        var shallowBefore = harness.Probe.ShallowProbeCalls.Count;

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        // A repair click for some other problem is not "an explicit user action on this host"
        // (ADR-015): it neither remounts the drive the user took down nor clears the park.
        Assert.True(harness.Snapshot("prod").UserParked);
        Assert.Equal(mountCalls, harness.Rclone.MountCalls.Count);
        Assert.Equal(MountState.Ready, harness.Snapshot("prod").State);

        // It is re-probed like every other idle host, so its reachability shown is fresh.
        Assert.Equal(shallowBefore + 1, harness.Probe.ShallowProbeCalls.Count);
    }

    // -- On-demand and mode = none --------------------------------------------------------------

    [Fact]
    public async Task A_mounted_on_demand_host_is_unmounted_and_rests_in_Ready_until_requested()
    {
        var harness = Harness(HostFixtures.OnDemand("archive", drive: "Q:"));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        Assert.Equal(["Q:"], harness.Rclone.UnmountCalls);
        Assert.Single(harness.Rclone.MountCalls); // the user's own mount; the repair added none
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        Assert.False(harness.Snapshot("archive").UserParked);

        // ... and it still mounts when asked.
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task A_host_with_mode_none_is_left_alone()
    {
        var harness = Harness(HostFixtures.None("ignored"), HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        var probesBefore = harness.Probe.ShallowProbeCalls.Count(h => h == Hostname("ignored"));

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        Assert.Equal(MountState.Disabled, harness.Snapshot("ignored").State);
        Assert.Equal(probesBefore, harness.Probe.ShallowProbeCalls.Count(h => h == Hostname("ignored")));
    }

    // -- Order, and "Disabled only after a confirmed unmount" ----------------------------------

    [Fact]
    public async Task Every_mounted_host_is_drained_before_any_host_is_probed_again_or_remounted()
    {
        var harness = Harness(
            HostFixtures.Persistent("alpha", drive: "P:"),
            HostFixtures.Persistent("bravo", drive: "R:"),
            HostFixtures.OnDemand("charlie", drive: "Q:"));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("charlie"));
        var baseline = harness.Supervisor.GetTransitionHistory().Count;

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        var entries = AfterBaseline(harness, baseline);
        var lastDrainConfirmed = entries.FindLastIndex(e => e.From == MountState.Draining && e.To == MountState.Disabled);
        var firstReprobe = entries.FindIndex(e => e.To == MountState.Probing);
        Assert.Equal(3, entries.Count(e => e.From == MountState.Draining && e.To == MountState.Disabled));
        Assert.True(lastDrainConfirmed >= 0 && firstReprobe >= 0);
        Assert.True(lastDrainConfirmed < firstReprobe, "a host was re-probed before every mounted host had drained");
        Assert.Equal(["P:", "Q:", "R:"], harness.Rclone.UnmountCalls.Order());
        Assert.Equal(MountState.Mounted, harness.Snapshot("alpha").State);
        Assert.Equal(MountState.Mounted, harness.Snapshot("bravo").State);
        Assert.Equal(MountState.Ready, harness.Snapshot("charlie").State);
    }

    [Fact]
    public async Task A_host_whose_unmount_is_not_confirmed_stays_Draining_and_is_not_remounted()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        harness.Rclone.ConfirmUnmountAfterCall("P:", callNumber: 1000); // rclone never says it is gone

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        // The state machine does not believe a drive is gone while rclone still lists it, so it
        // neither reaches Disabled nor remounts over the top of it.
        Assert.Equal(MountState.Draining, harness.Snapshot("prod").State);
        Assert.Single(harness.Rclone.MountCalls);
        Assert.DoesNotContain(
            harness.Supervisor.GetTransitionHistory(),
            e => e.HostKey == "prod" && e.From == MountState.Draining && e.To == MountState.Disabled);
    }

    [Fact]
    public async Task A_drain_that_confirms_later_still_ends_in_a_fresh_probe_and_a_remount()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        harness.Rclone.ConfirmUnmountAfterCall("P:", callNumber: 3);

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());
        Assert.Equal(MountState.Draining, harness.Snapshot("prod").State);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.False(harness.Snapshot("prod").UserParked);
    }

    [Fact]
    public async Task Mounting_is_only_ever_entered_from_Ready_during_a_repair_and_Mounted_never_skips_Draining()
    {
        var harness = Harness(
            HostFixtures.Persistent("alpha", drive: "P:"),
            HostFixtures.Persistent("bravo", drive: "R:"),
            HostFixtures.OnDemand("charlie", drive: "Q:"));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("charlie"));
        harness.Probe.EnqueueShallow(Hostname("bravo"), ShallowProbeOutcome.Timeout);

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());
        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));

        var history = harness.Supervisor.GetTransitionHistory();
        Assert.All(history.Where(e => e.To == MountState.Mounting), e => Assert.Equal(MountState.Ready, e.From));

        // Every drain-confirmed Disabled follows Draining, per host, and (for the mounted hosts
        // here) none is reached from Mounted directly.
        Assert.DoesNotContain(history, e => e.From == MountState.Mounted && e.To == MountState.Disabled);
    }

    // -- Suspend, and a failing mount -----------------------------------------------------------

    [Fact]
    public async Task A_repair_while_the_system_is_suspended_does_nothing()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        var unmounts = harness.Rclone.UnmountCalls.Count;
        var mounts = harness.Rclone.MountCalls.Count;

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        Assert.Equal(unmounts, harness.Rclone.UnmountCalls.Count);
        Assert.Equal(mounts, harness.Rclone.MountCalls.Count);
        Assert.Equal(MountState.Disabled, harness.Snapshot("prod").State);
    }

    [Fact]
    public async Task A_host_waiting_out_a_failed_mount_gets_a_fresh_attempt_straight_away()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        harness.Rclone.MakeMountFail("P:", new InvalidOperationException("drive letter in use"));
        await harness.StartAsync();
        Assert.Equal(MountState.Disabled, harness.Snapshot("prod").State); // paced: waiting for its retry timer
        Assert.Single(harness.Rclone.MountCalls);

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        // No clock advance: the repair is a fresh start. (The scripted failure was one-shot.)
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
    }

    // -- "Restart rclone": the supervisor must learn that rcd's mounts are gone from listmounts ---

    [Fact]
    public async Task After_rcd_is_restarted_the_supervisor_believes_its_mounts_until_it_reconciles()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();

        harness.Rclone.SimulateMountLost("P:"); // the old rcd died; the fresh one has no mounts

        // Not assumed: until something asks rclone, the supervisor's picture is unchanged.
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Single(harness.Rclone.MountCalls);
    }

    [Fact]
    public async Task Reconciling_after_an_rcd_restart_remounts_persistent_hosts_after_a_fresh_probe_and_leaves_on_demand_ones_down()
    {
        var harness = Harness(
            HostFixtures.Persistent("prod", drive: "P:"),
            HostFixtures.OnDemand("archive", drive: "Q:"));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        var shallowBefore = harness.Probe.ShallowProbeCalls.Count;
        var deepBefore = harness.Probe.DeepProbeCalls.Count(k => k == "prod");

        harness.Rclone.SimulateMountLost("P:");
        harness.Rclone.SimulateMountLost("Q:");
        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(2, harness.Rclone.MountCalls.Count(m => m.MountPoint == "P:"));
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        Assert.Single(harness.Rclone.MountCalls, m => m.MountPoint == "Q:");
        Assert.False(harness.Snapshot("prod").UserParked);
        Assert.False(harness.Snapshot("archive").UserParked);

        // The remount was re-authorised: both hosts re-probed shallow, the persistent one deep.
        Assert.Equal(shallowBefore + 2, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal(deepBefore + 1, harness.Probe.DeepProbeCalls.Count(k => k == "prod"));
    }

    // ---------------------------------------------------------------------------------------------

    private static SupervisorHarness Harness(params Bosun.Configuration.HostConfig[] hosts) =>
        new(HostFixtures.Build(HostFixtures.Global(), hosts));

    /// <summary>Transitions recorded since <paramref name="baselineCount"/> entries existed, oldest first.</summary>
    private static List<MountTransitionEntry> AfterBaseline(SupervisorHarness harness, int baselineCount)
    {
        var all = harness.Supervisor.GetTransitionHistory().Reverse().ToList();
        return all.Skip(baselineCount).ToList();
    }
}
