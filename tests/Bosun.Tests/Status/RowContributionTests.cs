using Bosun.Configuration;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.UI.Fakes;

namespace Bosun.Tests.Status;

/// <summary>
/// <see cref="StatusDerivation.ContributionOf"/> (bs-ww9.1.2): the per-row half of the roll-up, which the
/// tray tooltip uses to name the host behind the icon. The roll-up's own table is in
/// <see cref="StatusDerivationTests"/>; these pin that the two agree.
/// </summary>
public sealed class RowContributionTests
{
    [Fact]
    public void A_persistent_mount_failure_is_an_Error_but_an_on_demand_one_is_not()
    {
        Assert.Equal(AggregateHealth.Error, StatusDerivation.ContributionOf(HostRowFactory.Row(StatusCategory.MountFailing, mode: MountMode.Persistent)));
        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.ContributionOf(HostRowFactory.Row(StatusCategory.MountFailing, mode: MountMode.OnDemand)));
    }

    [Fact]
    public void Parked_and_on_demand_idle_contribute_nothing()
    {
        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.ContributionOf(HostRowFactory.Parked()));
        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.ContributionOf(HostRowFactory.Row(StatusCategory.OnDemandIdle)));
    }

    [Fact]
    public void A_mounted_host_with_a_stale_remote_is_Degraded_and_an_unmounted_persistent_one_is_Error()
    {
        Assert.Equal(
            AggregateHealth.Degraded,
            StatusDerivation.ContributionOf(HostRowFactory.Row(StatusCategory.ProvisioningFailed, state: MountState.Mounted)));
        Assert.Equal(
            AggregateHealth.Error,
            StatusDerivation.ContributionOf(HostRowFactory.Row(StatusCategory.ProvisioningFailed, state: MountState.Ready)));
    }

    [Theory]
    [InlineData(MountMode.Persistent)]
    [InlineData(MountMode.OnDemand)]
    public void A_mounted_host_with_failed_uploads_is_Degraded_for_either_tier(MountMode mode)
    {
        // Maintainer decision 2026-11-02 (bs-t12).
        Assert.Equal(
            AggregateHealth.Degraded,
            StatusDerivation.ContributionOf(
                HostRowFactory.Row(StatusCategory.UploadsFailing, state: MountState.Mounted, mode: mode)));
    }

    [Fact]
    public void The_roll_up_is_the_worst_row_contribution()
    {
        HostStatusRow[] rows =
        [
            HostRowFactory.Mounted(),
            HostRowFactory.Unreachable(),
            HostRowFactory.Parked(),
        ];

        Assert.Equal(rows.Max(StatusDerivation.ContributionOf), StatusDerivation.DeriveAggregateHealth(rows));
        Assert.Equal(AggregateHealth.Degraded, StatusDerivation.DeriveAggregateHealth(rows));
    }
}
