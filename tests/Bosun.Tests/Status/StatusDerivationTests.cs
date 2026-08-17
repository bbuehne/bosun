using Bosun.Configuration;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Status;

/// <summary>
/// Table-driven coverage of <see cref="StatusDerivation"/> (bs-ww9.6): every snapshot shape from
/// the brief's six causes, plus the precedence rules that decide which wins when more than one is
/// simultaneously true, plus the aggregate-health rollup. This is the highest-value part of the
/// deliverable -- a wrong derivation here is what sends the user to blame their VPN (ADR-012
/// Decision 3).
/// </summary>
public sealed class StatusDerivationTests
{
    private static readonly HostConfig PersistentHost = HostFixtures.Persistent("prod", drive: "P:");
    private static readonly HostConfig OnDemandHost = HostFixtures.OnDemand("archive", drive: "Q:");
    private static readonly HostConfig NoneHost = HostFixtures.None("jump");

    private static HostMountSnapshot Snapshot(
        string hostKey = "prod",
        MountState state = MountState.Ready,
        bool administrativelyEnabled = true,
        int consecutiveMountedFailures = 0,
        int consecutiveDeepProbeFailures = 0,
        int consecutiveIdleFailures = 0,
        bool userParked = false,
        string? mountUnavailableReason = null,
        int consecutiveMountFailures = 0,
        string? lastMountFailureReason = null) => new()
    {
        HostKey = hostKey,
        State = state,
        AdministrativelyEnabled = administrativelyEnabled,
        ConsecutiveMountedFailures = consecutiveMountedFailures,
        ConsecutiveDeepProbeFailures = consecutiveDeepProbeFailures,
        ConsecutiveIdleFailures = consecutiveIdleFailures,
        UserParked = userParked,
        MountUnavailableReason = mountUnavailableReason,
        ConsecutiveMountFailures = consecutiveMountFailures,
        LastMountFailureReason = lastMountFailureReason,
    };

    // ------------------------------------------------------------------------------------------
    // The six causes, one test each
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Case5_Mounted_is_Healthy()
    {
        var row = StatusDerivation.DeriveRow(Snapshot(state: MountState.Mounted), PersistentHost, sessionCount: 2);

        Assert.Equal(StatusCategory.Healthy, row.Category);
        Assert.Equal("P: is mounted", row.StatusText);
        Assert.Equal(2, row.SessionCount);
    }

    [Fact]
    public void Case2_Unreachable_reports_the_consecutive_failure_count()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(state: MountState.Unreachable, consecutiveIdleFailures: 4), PersistentHost, 0);

        Assert.Equal(StatusCategory.Unreachable, row.Category);
        Assert.Equal("P: is not mounted -- host unreachable after 4 consecutive failed probes", row.StatusText);
    }

    [Fact]
    public void Case2_Unreachable_uses_singular_probe_wording_for_a_count_of_one()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(state: MountState.Unreachable, consecutiveIdleFailures: 1), PersistentHost, 0);

        Assert.Equal("P: is not mounted -- host unreachable after 1 consecutive failed probe", row.StatusText);
    }

    [Fact]
    public void Case3_bsww94_reachable_but_mount_failing_surfaces_count_and_last_error()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(
                state: MountState.Disabled,
                consecutiveMountFailures: 4,
                lastMountFailureReason: "mount/mount failed: access denied"),
            PersistentHost, 0);

        Assert.Equal(StatusCategory.MountFailing, row.Category);
        Assert.Equal(
            "P: is not mounted -- mount failed 4 times; last error: mount/mount failed: access denied",
            row.StatusText);
    }

    [Fact]
    public void Case3_uses_singular_time_wording_for_a_count_of_one()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(
                state: MountState.Ready,
                consecutiveMountFailures: 1,
                lastMountFailureReason: "deep probe failed entering Mounting: Timeout"),
            PersistentHost, 0);

        Assert.Equal(
            "P: is not mounted -- mount failed 1 time; last error: deep probe failed entering Mounting: Timeout",
            row.StatusText);
    }

    [Fact]
    public void Case3_falls_back_to_unknown_error_rather_than_a_blank_reason()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(state: MountState.Ready, consecutiveMountFailures: 1, lastMountFailureReason: null),
            PersistentHost, 0);

        Assert.Equal(StatusCategory.MountFailing, row.Category);
        Assert.Contains("unknown error", row.StatusText);
    }

    [Fact]
    public void Case4_Parked_renders_as_deliberate_never_as_a_fault()
    {
        var row = StatusDerivation.DeriveRow(Snapshot(state: MountState.Ready, userParked: true), PersistentHost, 0);

        Assert.Equal(StatusCategory.Parked, row.Category);
        Assert.Equal(
            "P: is parked -- you unmounted it; mount it again from the tray when you're ready",
            row.StatusText);
    }

    [Fact]
    public void Case1_process_wide_unavailability_matches_ADR012s_own_worked_example()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(state: MountState.Ready, mountUnavailableReason: "WinFsp is not installed"), PersistentHost, 0);

        Assert.Equal(StatusCategory.MountingUnavailable, row.Category);
        Assert.Equal("P: is not mounted -- WinFsp is not installed", row.StatusText);
    }

    [Fact]
    public void Case6_on_demand_resting_in_Ready_is_not_a_fault()
    {
        var row = StatusDerivation.DeriveRow(Snapshot(hostKey: "archive", state: MountState.Ready), OnDemandHost, 0);

        Assert.Equal(StatusCategory.OnDemandIdle, row.Category);
        Assert.Equal("Q: is not mounted -- on-demand; mount it from the tray when you need it", row.StatusText);
    }

    [Fact]
    public void Mode_none_is_NotConfigured_not_one_of_the_six_causes()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(hostKey: "jump", state: MountState.Disabled, administrativelyEnabled: false), NoneHost, 0);

        Assert.Equal(StatusCategory.NotConfigured, row.Category);
    }

    [Theory]
    [InlineData(MountState.Probing)]
    [InlineData(MountState.Mounting)]
    [InlineData(MountState.Draining)]
    [InlineData(MountState.Disabled)]
    public void Transitional_states_with_no_fault_signal_are_Pending_not_a_fault_category(MountState state)
    {
        var row = StatusDerivation.DeriveRow(Snapshot(state: state), PersistentHost, 0);

        Assert.Equal(StatusCategory.Pending, row.Category);
    }

    // ------------------------------------------------------------------------------------------
    // Precedence: which cause wins when more than one is simultaneously true
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Mounted_wins_over_a_stale_nonzero_mount_failure_count()
    {
        // ConsecutiveMountFailures only resets to zero on a successful mount -- and this snapshot
        // IS that successful mount, still carrying the count from the failing attempts before it.
        var row = StatusDerivation.DeriveRow(
            Snapshot(state: MountState.Mounted, consecutiveMountFailures: 3, lastMountFailureReason: "stale"),
            PersistentHost, 0);

        Assert.Equal(StatusCategory.Healthy, row.Category);
    }

    [Fact]
    public void Parked_wins_over_process_wide_unavailability()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(state: MountState.Ready, userParked: true, mountUnavailableReason: "WinFsp is not installed"),
            PersistentHost, 0);

        Assert.Equal(StatusCategory.Parked, row.Category);
    }

    [Fact]
    public void Process_wide_unavailability_wins_over_a_stale_mount_failure_count()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(
                state: MountState.Ready,
                mountUnavailableReason: "rclone rcd is not healthy",
                consecutiveMountFailures: 2,
                lastMountFailureReason: "stale"),
            PersistentHost, 0);

        Assert.Equal(StatusCategory.MountingUnavailable, row.Category);
    }

    [Fact]
    public void Currently_Unreachable_wins_over_a_stale_mount_failure_count_the_freshest_signal_rule()
    {
        // The host can carry a nonzero ConsecutiveMountFailures from an earlier cycle while having
        // since gone genuinely Unreachable for an unrelated reason. Unreachable must win: it is the
        // more current, more actionable fact -- reporting the stale count instead would be the
        // exact misdirection ADR-012 Decision 3 exists to prevent.
        var row = StatusDerivation.DeriveRow(
            Snapshot(
                state: MountState.Unreachable,
                consecutiveIdleFailures: 2,
                consecutiveMountFailures: 3,
                lastMountFailureReason: "stale"),
            PersistentHost, 0);

        Assert.Equal(StatusCategory.Unreachable, row.Category);
    }

    [Fact]
    public void On_demand_mount_failure_still_surfaces_as_MountFailing_not_as_OnDemandIdle()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(
                hostKey: "archive",
                state: MountState.Ready,
                consecutiveMountFailures: 2,
                lastMountFailureReason: "mount/mount failed: drive letter in use"),
            OnDemandHost, 0);

        Assert.Equal(StatusCategory.MountFailing, row.Category);
    }

    // ------------------------------------------------------------------------------------------
    // Aggregate health rollup
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Aggregate_Healthy_when_every_host_is_fine()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(Snapshot(state: MountState.Mounted), PersistentHost, 0),
            StatusDerivation.DeriveRow(Snapshot(hostKey: "archive", state: MountState.Ready), OnDemandHost, 0),
        };

        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_Error_for_process_wide_unavailability()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(mountUnavailableReason: "WinFsp is not installed"), PersistentHost, 0),
        };

        Assert.Equal(AggregateHealth.Error, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_Error_for_a_persistent_host_repeatedly_failing_to_mount()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(state: MountState.Disabled, consecutiveMountFailures: 5, lastMountFailureReason: "x"),
                PersistentHost, 0),
        };

        Assert.Equal(AggregateHealth.Error, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_does_NOT_escalate_for_an_on_demand_host_repeatedly_failing_to_mount()
    {
        // bs-ww9.4 item 3: an on-demand mount failure already got immediate feedback from the
        // click that caused it, so it must not also push the tray icon into an error state.
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(hostKey: "archive", state: MountState.Ready, consecutiveMountFailures: 5, lastMountFailureReason: "x"),
                OnDemandHost, 0),
        };

        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_Degraded_for_a_persistent_host_merely_Unreachable()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(state: MountState.Unreachable, consecutiveIdleFailures: 2), PersistentHost, 0),
        };

        Assert.Equal(AggregateHealth.Degraded, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_does_NOT_degrade_for_an_on_demand_host_Unreachable()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(hostKey: "archive", state: MountState.Unreachable, consecutiveIdleFailures: 2), OnDemandHost, 0),
        };

        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_Degraded_for_a_Mounted_host_accumulating_shallow_probe_failures()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(state: MountState.Mounted, consecutiveMountedFailures: 2), PersistentHost, 0),
        };

        Assert.Equal(AggregateHealth.Degraded, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_Degraded_for_a_Mounted_host_accumulating_deep_probe_failures()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(
                Snapshot(state: MountState.Mounted, consecutiveDeepProbeFailures: 1), PersistentHost, 0),
        };

        Assert.Equal(AggregateHealth.Degraded, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_does_NOT_degrade_for_a_Parked_host()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(Snapshot(state: MountState.Ready, userParked: true), PersistentHost, 0),
        };

        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.DeriveAggregateHealth(rows));
    }

    [Fact]
    public void Aggregate_does_NOT_degrade_for_an_on_demand_host_resting_in_Ready()
    {
        var rows = new[]
        {
            StatusDerivation.DeriveRow(Snapshot(hostKey: "archive", state: MountState.Ready), OnDemandHost, 0),
        };

        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.DeriveAggregateHealth(rows));
    }
}
