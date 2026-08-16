using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// docs/ARCHITECTURE.md §6 ("Bosun crashes with mounts up: on next start, enumerate existing
/// mounts... and adopt or clear them before doing anything else") and §4 "Reconciliation" ("every
/// supervisor tick, compare intended state against mount/listmounts... reconcile toward intended
/// state; log every correction"), plus the <c>RcloneProcessService.StatusChanged</c>-to-Healthy
/// contract ("a restarted rcd knows nothing about mounts your state believes in").
/// </summary>
public sealed class ReconciliationAndCrashRecoveryTests
{
    [Fact]
    public async Task Startup_adopts_a_mount_matching_a_configured_host_without_probing_it_first()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.SeedExistingMount("P:", "bosun-prod:/srv/share");

        await harness.StartAsync();

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        // Adopted, not (re)probed to get there -- I1 is about authorising a NEW transition into
        // Mounting via a fresh deep probe; adoption recognises reality directly (see the remarks
        // on MountSupervisor.AdoptOrClearExistingMountsAsync) and relies on the immediately-armed
        // continuous Mounted-probe cadence (ADR-011) to catch a genuinely dead adopted mount.
        Assert.Empty(harness.Probe.DeepProbeCalls);
        Assert.Empty(harness.Rclone.MountCalls);
    }

    [Fact]
    public async Task Adopted_mount_is_still_continuously_probed_and_can_still_drain_on_failure()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 1, mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        // Must match the host's actual configured remote (bosun-<key>:<RemotePath>, "/srv/share"
        // per HostFixtures) now that adoption is fs-aware (bs-fix-e5 defect 2) -- otherwise this
        // would be cleared and remounted rather than adopted, which is a different test.
        harness.Rclone.SeedExistingMount("Q:", "bosun-archive:/srv/share");
        harness.Probe.SetDefaultShallow(Bosun.Probe.ShallowProbeOutcome.OtherFailure);

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        // The fakes default back to success once re-enabled, so within this one Advance+Drain
        // pass the host actually completes Mounted -> Draining -> Disabled -> Probing ->
        // Unreachable (the re-enable probe fails too, same fake outcome) -- asserting the unmount
        // call is what proves the drain happened. See FailuresBeforeUnmountClampTests for the
        // same pattern.
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Startup_clears_an_orphaned_mount_with_no_matching_configured_host()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.SeedExistingMount("Z:", "bosun-unknown:/srv"); // no host claims Z:

        await harness.StartAsync();

        Assert.Contains("Z:", harness.Rclone.UnmountCalls);
    }

    [Fact]
    public async Task Startup_clears_a_mount_for_a_host_now_configured_as_mode_none()
    {
        // Deliberately NOT HostFixtures.None (which nulls Drive) -- to exercise the "a host WAS
        // found by drive match, but its mode is now none" branch specifically, this host keeps a
        // configured Drive while having mode = none (the config a user reconfiguring away from
        // mounting, without also clearing the stale [mount] block, could plausibly leave behind).
        var jumpMounting = HostFixtures.Persistent("jump", drive: "Z:");
        var jump = jumpMounting with { Mount = jumpMounting.Mount with { Mode = MountMode.None } };
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), jump));
        harness.Rclone.SeedExistingMount("Z:", "bosun-jump:/srv");

        await harness.StartAsync();

        Assert.Contains("Z:", harness.Rclone.UnmountCalls);
        Assert.Equal(MountState.Disabled, harness.Snapshot("jump").State);
    }

    [Fact]
    public async Task Periodic_reconciliation_drains_a_host_whose_mount_rclone_silently_lost()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        harness.Rclone.SimulateMountLost("P:"); // rclone lost it without Bosun calling unmount

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // ReconciliationInterval

        // Persistent tier: the fakes default to success, so within this one Advance+Drain pass
        // the host completes Mounted -> Draining -> Disabled -> Probing -> Ready -> Mounting ->
        // Mounted again (persistent auto-mounts on every arrival at Ready -- rule 6). That full
        // recycle IS the proof reconciliation detected and corrected the drift: a second,
        // independent mount/mount call happened, which could only happen after the drift was
        // first drained.
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("P:"));
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
    }

    [Fact]
    public async Task Periodic_reconciliation_clears_an_orphaned_mount_for_a_host_that_is_not_Mounted()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        // Simulate an out-of-band mount appearing at the host's drive letter while it is Ready.
        harness.Rclone.SeedExistingMount("Q:", "bosun-archive:/srv");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Q:", harness.Rclone.UnmountCalls);
    }

    [Fact]
    public async Task OnRcloneRestartedAsync_reconciles_immediately_without_waiting_for_the_next_tick()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        // A freshly-restarted rcd's own listmounts is authoritatively empty; simulate that for
        // this host specifically.
        harness.Rclone.SimulateMountLost("P:");

        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        // Same full-recycle reasoning as Periodic_reconciliation_drains_a_host_whose_mount_
        // rclone_silently_lost: a second mount/mount call proves the drift was detected and
        // corrected immediately, not left until the next 30s reconciliation tick.
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("P:"));
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
    }
}
