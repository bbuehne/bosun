using Bosun.Rclone;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Status;

/// <summary>
/// ADR-025 (bs-k9x): how a host-key refusal reads in the status row, and what it does to the
/// aggregate icon. It is a mount failure on a host that answers, so never "unreachable".
/// </summary>
public sealed class HostKeyStatusTests
{
    private static HostMountSnapshot Snapshot(HostKeyFailureKind kind, MountState state = MountState.Disabled) => new()
    {
        HostKey = "prod",
        State = state,
        AdministrativelyEnabled = true,
        ConsecutiveMountFailures = 3,
        LastMountFailureReason = HostKeyFailure.Describe(kind),
        LastMountFailureHostKey = kind,
    };

    [Theory]
    [InlineData(HostKeyFailureKind.UnknownKey)]
    [InlineData(HostKeyFailureKind.ChangedKey)]
    [InlineData(HostKeyFailureKind.KnownHostsMissing)]
    public void Each_kind_shows_its_own_sentence_as_a_mount_failure_not_as_unreachable(HostKeyFailureKind kind)
    {
        var row = StatusDerivation.DeriveRow(Snapshot(kind), HostFixtures.Persistent("prod", drive: "P:"), 0);

        Assert.Equal(StatusCategory.MountFailing, row.Category);
        Assert.Equal($"P: is not mounted -- {HostKeyFailure.Describe(kind)}", row.StatusText);
        Assert.DoesNotContain("unreachable", row.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mount failed", row.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_persistent_host_refused_for_its_key_turns_the_icon_to_Error_because_it_will_not_resolve_itself()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(HostKeyFailureKind.UnknownKey), HostFixtures.Persistent("prod", drive: "P:"), 0);

        Assert.Equal(AggregateHealth.Error, StatusDerivation.DeriveAggregateHealth([row]));
    }

    [Fact]
    public void An_on_demand_host_refused_for_its_key_does_not_escalate_the_icon()
    {
        var row = StatusDerivation.DeriveRow(
            Snapshot(HostKeyFailureKind.UnknownKey), HostFixtures.OnDemand("prod", drive: "P:"), 0);

        Assert.Equal(StatusCategory.MountFailing, row.Category);
        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.DeriveAggregateHealth([row]));
    }

    [Fact]
    public void A_mount_failure_of_another_kind_keeps_the_existing_wording()
    {
        var snapshot = new HostMountSnapshot
        {
            HostKey = "prod",
            State = MountState.Disabled,
            AdministrativelyEnabled = true,
            ConsecutiveMountFailures = 2,
            LastMountFailureReason = "mount/mount failed: drive letter already in use",
        };

        var row = StatusDerivation.DeriveRow(snapshot, HostFixtures.Persistent("prod", drive: "P:"), 0);

        Assert.Equal("P: is not mounted -- mount failed 2 times; last error: mount/mount failed: drive letter already in use", row.StatusText);
    }
}
