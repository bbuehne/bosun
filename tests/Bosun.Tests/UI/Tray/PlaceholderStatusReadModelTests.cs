using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.UI.Tray.Fakes;
using Bosun.UI.Tray;
using Bosun.UI.Tray.Placeholder;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// Light coverage of the TEMPORARY placeholder read-model -- see its own class remarks for why
/// this is deliberately not held to bs-ww9.6's causal-text bar. Enough to pin that the seam
/// actually works end to end (join against config, aggregate health rules) while it is in place.
/// </summary>
public sealed class PlaceholderStatusReadModelTests
{
    private static HostMountSnapshot Snapshot(
        string hostKey,
        MountState state,
        bool administrativelyEnabled = true,
        bool userParked = false,
        string? drive = null,
        string? mountUnavailableReason = null) => new()
    {
        HostKey = hostKey,
        State = state,
        Drive = drive,
        AdministrativelyEnabled = administrativelyEnabled,
        UserParked = userParked,
        MountUnavailableReason = mountUnavailableReason,
    };

    [Fact]
    public void GetRows_UsesTheConfiguredDisplayName()
    {
        var config = HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("example-nas"));
        var configStore = new FakeHostConfigStore(config);
        var supervisor = new FakeMountSupervisor { Snapshot = [Snapshot("example-nas", MountState.Ready)] };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        var row = Assert.Single(model.GetRows());

        Assert.Equal("example-nas", row.HostKey);
        Assert.Equal(config.Hosts["example-nas"].DisplayName, row.DisplayName);
    }

    [Fact]
    public void GetRows_FallsBackToTheHostKey_WhenNoMatchingConfigEntryExists()
    {
        // Can happen if the snapshot briefly lags a config reload; must not throw.
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var supervisor = new FakeMountSupervisor { Snapshot = [Snapshot("orphaned-host", MountState.Ready)] };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        var row = Assert.Single(model.GetRows());

        Assert.Equal("orphaned-host", row.DisplayName);
    }

    [Fact]
    public void GetRows_OnlyReportsADrive_WhenActuallyMounted()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("nas")));
        var supervisor = new FakeMountSupervisor { Snapshot = [Snapshot("nas", MountState.Ready, drive: "P:")] };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        var row = Assert.Single(model.GetRows());

        Assert.Null(row.Drive);
    }

    [Fact]
    public void GetRows_ReportsTheDrive_WhenMounted()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("nas")));
        var supervisor = new FakeMountSupervisor { Snapshot = [Snapshot("nas", MountState.Mounted, drive: "P:")] };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        var row = Assert.Single(model.GetRows());

        Assert.Equal("P:", row.Drive);
    }

    [Fact]
    public void GetRows_MarksAParkedHostAsDeliberate_NotAsAFault()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("nas")));
        var supervisor = new FakeMountSupervisor { Snapshot = [Snapshot("nas", MountState.Ready, userParked: true)] };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        var row = Assert.Single(model.GetRows());

        Assert.True(row.IsParked);
        Assert.DoesNotContain("fault", row.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAggregateHealth_IsMountingUnavailable_WhenAnyHostCarriesAProcessWideReason()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var supervisor = new FakeMountSupervisor
        {
            Snapshot = [Snapshot("nas", MountState.Ready, mountUnavailableReason: "WinFsp is not installed")],
        };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        Assert.Equal(AggregateHealth.MountingUnavailable, model.GetAggregateHealth());
    }

    [Fact]
    public void GetAggregateHealth_IsDegraded_WhenAnEnabledNonParkedHostIsUnreachable()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var supervisor = new FakeMountSupervisor { Snapshot = [Snapshot("nas", MountState.Unreachable)] };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        Assert.Equal(AggregateHealth.Degraded, model.GetAggregateHealth());
    }

    [Fact]
    public void GetAggregateHealth_IsHealthy_WhenAParkedHostIsUnreachable()
    {
        // ADR-015: a parked host is deliberately not mounted; it must not drag the whole tray icon
        // into a degraded state just because it is also currently unreachable.
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var supervisor = new FakeMountSupervisor
        {
            Snapshot = [Snapshot("nas", MountState.Unreachable, userParked: true)],
        };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        Assert.Equal(AggregateHealth.Healthy, model.GetAggregateHealth());
    }

    [Fact]
    public void GetAggregateHealth_IsHealthy_WhenEverythingIsMountedOrReady()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var supervisor = new FakeMountSupervisor
        {
            Snapshot =
            [
                Snapshot("nas", MountState.Mounted, drive: "P:"),
                Snapshot("jump", MountState.Ready),
            ],
        };
        var model = new PlaceholderStatusReadModel(supervisor, configStore);

        Assert.Equal(AggregateHealth.Healthy, model.GetAggregateHealth());
    }

    [Fact]
    public void Constructor_Throws_ForNullDependencies()
    {
        var configStore = new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var supervisor = new FakeMountSupervisor();

        Assert.Throws<ArgumentNullException>(() => new PlaceholderStatusReadModel(null!, configStore));
        Assert.Throws<ArgumentNullException>(() => new PlaceholderStatusReadModel(supervisor, null!));
    }
}
