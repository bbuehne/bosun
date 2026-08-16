using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-yvw.1: the mounting-availability gate. ADR-012 and docs/ARCHITECTURE.md §6 both say WinFsp
/// absent means mount features are disabled while terminal features (and, per ADR-012 Decision 2,
/// idle reachability probing) keep working -- but nothing previously enforced that at the
/// <c>MountSupervisor</c> level. Without a gate, a persistent host would pass its shallow probe,
/// pass its deep probe (SFTP <c>operations/list</c> needs neither WinFsp nor rclone), call
/// <c>mount/mount</c>, fail, drain, back off, and retry forever against a condition only
/// installing software can fix -- the shape of bs-k4k again, paced rather than spinning.
/// </summary>
public sealed class MountingAvailabilityTests
{
    private static readonly MountingAvailability WinFspMissing =
        MountingAvailability.Unavailable(MountingUnavailableCause.WinFspMissing, "WinFsp is not installed");

    [Fact]
    public async Task Persistent_host_probes_normally_reaches_Ready_and_never_calls_mount_while_unavailable()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(WinFspMissing));
        await harness.StartAsync();

        // The behaviour that matters is the call count, not the eventual state (a host that never
        // calls mount/mount could still coincidentally end up somewhere that looks safe for the
        // wrong reason).
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Equal(MountState.Ready, harness.Snapshot("prod").State);
        Assert.NotEmpty(harness.Probe.ShallowProbeCalls);
    }

    [Fact]
    public async Task Snapshot_carries_the_causal_WinFsp_reason()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(WinFspMissing));
        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal("WinFsp is not installed", snapshot.MountUnavailableReason);
    }

    [Fact]
    public async Task Snapshot_reason_generalises_to_rclone_unhealthy_without_a_new_gate()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rclone rcd is not answering")));
        await harness.StartAsync();

        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Equal("rclone rcd is not answering", harness.Snapshot("prod").MountUnavailableReason);
    }

    [Fact]
    public async Task Snapshot_reason_is_null_once_mounting_is_available()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();

        Assert.Null(harness.Snapshot("archive").MountUnavailableReason);
    }

    [Fact]
    public async Task Mounting_becoming_available_mounts_a_blocked_persistent_host_without_a_restart()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(WinFspMissing));
        await harness.StartAsync();
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Equal(MountState.Ready, harness.Snapshot("prod").State);

        // rcd (or WinFsp) recovers -- StartupOrchestrator would push this from its
        // RcloneProcessService.StatusChanged subscription; nothing here restarts the process.
        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));

        Assert.Single(harness.Rclone.MountCalls);
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Null(harness.Snapshot("prod").MountUnavailableReason);
    }

    [Fact]
    public async Task OnDemand_hosts_explicit_mount_request_fails_loudly_with_the_causal_reason_while_unavailable()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(WinFspMissing));

        var ex = await Assert.ThrowsAsync<MountingUnavailableException>(
            () => harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive")));

        Assert.Equal("archive", ex.HostKey);
        Assert.Equal(MountingUnavailableCause.WinFspMissing, ex.Cause);
        Assert.Contains("WinFsp is not installed", ex.Message, StringComparison.Ordinal);

        // Not a silent no-op, but not a mount either -- ADR-014 rule 8's standard applied to this
        // gate, and Invariant I1 both hold at once.
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task OnDemand_hosts_explicit_mount_request_succeeds_immediately_once_the_gate_reopens()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(WinFspMissing));
        await Assert.ThrowsAsync<MountingUnavailableException>(
            () => harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive")));

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Single(harness.Rclone.MountCalls);
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task A_persistent_host_blocked_by_unavailability_never_issues_a_deep_probe_either()
    {
        // The gate is checked BEFORE the deep probe (not just before mount/mount), so a host whose
        // mount cannot possibly succeed does not keep hitting the remote SFTP subsystem every
        // retry for no reason -- exactly the "endless and noisy in the target server's auth log"
        // shape the brief calls out.
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.RunAsync(() => harness.Supervisor.SetMountingAvailabilityAsync(WinFspMissing));
        await harness.StartAsync();

        Assert.Empty(harness.Probe.DeepProbeCalls);
    }

    [Fact]
    public async Task Mounting_available_by_default_is_unchanged_from_pre_gate_behaviour()
    {
        // No test/production code that never touches SetMountingAvailabilityAsync should observe
        // any change -- this pins the default.
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();

        Assert.Single(harness.Rclone.MountCalls);
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Null(harness.Snapshot("prod").MountUnavailableReason);
    }
}
