using Bosun.Configuration;
using Bosun.Health;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.SessionMonitor;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.UI;
using Bosun.UI.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.UI;

/// <summary>
/// bs-4js, defect 2: the UI used to resolve <see cref="IMountSupervisor"/> (and the config store)
/// unconditionally, so the faults that most need reporting destroyed the tray icon and window that
/// report them. <c>App.InitializeUserInterface</c> now builds from
/// <see cref="UserInterfaceDependencyResolver"/>; these tests pin the rule it enforces -- a service
/// the container cannot build must cost the UI that one capability and nothing else -- and that what it
/// falls back to is safe to hand to the tray controller's collaborators. WPF itself is not
/// constructed (the tray and window are not unit-testable); everything they are built from is.
/// Every service here is a fake: no rclone, no drive letter, no real fragment path.
/// </summary>
public sealed class UserInterfaceDependencyResolverTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppHealth>(new StubAppHealth());
        return services;
    }

    private static UserInterfaceDependencies Resolve(IServiceCollection services) =>
        UserInterfaceDependencyResolver.Resolve(services.BuildServiceProvider(), NullLogger.Instance);

    [Fact]
    public void When_every_service_resolves_the_real_ones_are_used_and_nothing_is_a_stand_in()
    {
        var supervisor = new UnavailableMountSupervisor(); // any IMountSupervisor instance will do
        var store = new EmptyHostConfigStore();
        var sessions = new FakeSessionMonitor();
        var services = BaseServices();
        services.AddSingleton<IMountSupervisor>(supervisor);
        services.AddSingleton<IHostConfigStore>(store);
        services.AddSingleton<ISessionMonitor>(sessions);

        var deps = Resolve(services);

        Assert.Same(supervisor, deps.Supervisor);
        Assert.Same(store, deps.ConfigStore);
        Assert.Same(sessions, deps.SessionMonitor);
        Assert.False(deps.SupervisorIsStandIn);
        Assert.False(deps.ConfigStoreIsStandIn);
    }

    [Fact]
    public void A_supervisor_whose_construction_throws_does_not_stop_the_UI_dependencies_resolving()
    {
        var services = BaseServices();
        services.AddSingleton<IHostConfigStore>(new EmptyHostConfigStore());
        services.AddSingleton<ISessionMonitor>(new FakeSessionMonitor());
        services.AddSingleton<IMountSupervisor>(_ => throw new InvalidOperationException("supervisor cannot be built"));

        var deps = Resolve(services);

        Assert.True(deps.SupervisorIsStandIn);
        Assert.IsType<UnavailableMountSupervisor>(deps.Supervisor);
        // Everything that did resolve is kept: the failure costs one capability, not the lot.
        Assert.False(deps.ConfigStoreIsStandIn);
        Assert.IsType<FakeSessionMonitor>(deps.SessionMonitor);
    }

    [Fact]
    public void The_original_incident_an_unregistered_TimeProvider_makes_IProbe_and_so_the_supervisor_unbuildable_and_the_UI_still_builds()
    {
        // Exactly the shape BosunHostFactory registered when the bug shipped: IProbe is HostProbe,
        // which takes a TimeProvider, and nothing registered one.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppHealth>(new StubAppHealth());
        services.AddSingleton<IHostConfigStore>(new FakeConfigStore(HostFixtures.Build(HostFixtures.Global())));
        services.AddSingleton<IRcloneClient>(new FakeRcloneClient());
        services.AddSingleton<ITcpProbeTransport, TcpProbeTransport>();
        services.AddSingleton<IRemoteRootLister, NoopRemoteRootLister>();
        services.AddSingleton<IProbe, HostProbe>();
        services.AddSingleton(sp => new MountSupervisor(
            sp.GetRequiredService<IHostConfigStore>(),
            sp.GetRequiredService<IRcloneClient>(),
            sp.GetRequiredService<IProbe>(),
            new FakeDriveTypeReader(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<MountSupervisor>>()));
        services.AddSingleton<IMountSupervisor>(sp => sp.GetRequiredService<MountSupervisor>());

        // Precondition: this really is the unbuildable-supervisor fault, not a test of a fake.
        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMountSupervisor>());
        Assert.Contains("TimeProvider", ex.Message, StringComparison.Ordinal);

        var deps = UserInterfaceDependencyResolver.Resolve(provider, NullLogger.Instance);

        Assert.True(deps.SupervisorIsStandIn);
        Assert.NotNull(deps.AppHealth);
        // The clock the UI needs falls back to the system clock rather than being the thing that fails.
        Assert.Same(TimeProvider.System, deps.TimeProvider);
        // The config was fine, so it is kept.
        Assert.False(deps.ConfigStoreIsStandIn);
    }

    [Fact]
    public void An_invalid_config_which_makes_the_config_store_and_everything_built_on_it_throw_still_yields_a_UI()
    {
        // HostConfigStore.Load throws on an invalid first load; the supervisor, session monitor, writer
        // and diagnostics all sit on it. ADR-012 Decision 2 promises an error-state tray icon here.
        var services = BaseServices();
        services.AddSingleton<IHostConfigStore>(
            _ => throw new InvalidConfigException("hosts.toml", ["line 3, column 1: unexpected token"]));
        services.AddSingleton<IMountSupervisor>(sp => { _ = sp.GetRequiredService<IHostConfigStore>(); return new UnavailableMountSupervisor(); });
        services.AddSingleton<ISessionMonitor>(sp => { _ = sp.GetRequiredService<IHostConfigStore>(); return new FakeSessionMonitor(); });
        services.AddSingleton<IHostConfigWriter>(sp => { _ = sp.GetRequiredService<IHostConfigStore>(); return null!; });

        var deps = Resolve(services);

        Assert.True(deps.ConfigStoreIsStandIn);
        Assert.True(deps.SupervisorIsStandIn);
        Assert.IsType<EmptyHostConfigStore>(deps.ConfigStore);
        Assert.IsType<NoSessionMonitor>(deps.SessionMonitor);
        Assert.Null(deps.ConfigWriter); // no editor that cannot save
    }

    [Fact]
    public void A_service_that_is_simply_not_registered_is_treated_like_one_that_throws()
    {
        var deps = Resolve(BaseServices());

        Assert.True(deps.SupervisorIsStandIn);
        Assert.True(deps.ConfigStoreIsStandIn);
        Assert.IsType<NoSessionMonitor>(deps.SessionMonitor);
        Assert.Null(deps.Diagnostics);
        Assert.Null(deps.ConfigWriter);
        Assert.Null(deps.RestartNotice);
    }

    [Fact]
    public void The_health_model_is_not_stood_in_for_because_it_is_the_channel_that_reports_everything_else()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Assert.Throws<InvalidOperationException>(
            () => UserInterfaceDependencyResolver.Resolve(services.BuildServiceProvider(), NullLogger.Instance));
    }

    // ------------------------------------------------------------------------------------------
    // The stand-ins are safe to build the tray/window collaborators on
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_read_model_builds_and_polls_on_stand_ins_and_reports_no_rows_rather_than_throwing()
    {
        var time = new Bosun.Tests.Configuration.Fakes.FakeTimeProvider();
        var model = new StatusReadModel(
            new UnavailableMountSupervisor(),
            new EmptyHostConfigStore(),
            new NoSessionMonitor(),
            time,
            NullLogger<StatusReadModel>.Instance);

        model.Start();
        time.Advance(StatusReadModel.PollInterval);

        Assert.Empty(model.Current.Rows);
        Assert.Empty(model.Current.RecentTransitions);
        Assert.Equal(AggregateHealth.Healthy, model.Current.Health);
    }

    [Fact]
    public async Task Every_supervisor_stand_in_command_completes_without_throwing_and_without_a_row_to_act_on()
    {
        var supervisor = new UnavailableMountSupervisor();

        await supervisor.StartAsync();
        await supervisor.RequestMountAsync("example-nas");
        await supervisor.RequestUnmountAsync("example-nas");
        await supervisor.RequestRetryNowAsync("example-nas");
        await supervisor.RecordActivityAsync("example-nas");
        await supervisor.SuspendAsync();
        await supervisor.ResumeAsync();
        await supervisor.NetworkChangedAsync();
        await supervisor.RepairAllAsync();
        await supervisor.OnRcloneRestartedAsync();
        await supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available);
        await supervisor.ConfigChangedAsync(HostFixtures.Build(HostFixtures.Global()));
        await supervisor.StopAsync();

        Assert.Empty(supervisor.GetSnapshot());
        Assert.Empty(supervisor.GetTransitionHistory());
    }

    [Fact]
    public void The_action_dispatcher_built_on_the_stand_in_supervisor_does_not_throw_on_a_mount_click()
    {
        var dispatcher = new HostActionDispatcher(new UnavailableMountSupervisor(), new Bosun.Tests.UI.Tray.Fakes.FakeExternalLauncher());
        var row = new HostStatusRow
        {
            HostKey = "example-nas",
            DisplayName = "example-nas",
            State = MountState.Disabled,
            Drive = "P:",
            UserParked = false,
            StatusText = "irrelevant",
            Mode = MountMode.Persistent,
            SessionCount = 0,
            Category = StatusCategory.Pending,
        };

        var mount = Record.Exception(() => dispatcher.Dispatch(HostMenuActionKind.Mount, row));
        var unmount = Record.Exception(() => dispatcher.Dispatch(HostMenuActionKind.Unmount, row));
        // No diagnostics command was resolved (the supervisor it needs is gone): the menu item must
        // be a safe no-op, not an NRE on the UI thread.
        var diagnostics = Record.Exception(dispatcher.CopyDiagnostics);

        Assert.Null(mount);
        Assert.Null(unmount);
        Assert.Null(diagnostics);
    }

    private sealed class NoopRemoteRootLister : IRemoteRootLister
    {
        public Task ListRootAsync(string hostKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeConfigStore(BosunConfig config) : IHostConfigStore
    {
        public BosunConfig Current { get; } = config;

#pragma warning disable CS0067
        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

        public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067
    }

    private sealed class StubAppHealth : IAppHealth
    {
        public AppHealth Current => AppHealth.Healthy;

#pragma warning disable CS0067
        public event EventHandler<AppHealthChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }
}
