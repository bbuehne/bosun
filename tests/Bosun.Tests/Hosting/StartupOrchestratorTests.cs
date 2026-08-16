using Bosun.Configuration;
using Bosun.Hosting;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Supervisor;
using Bosun.Terminal;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Hosting.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Terminal.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers bs-6f9 (E12b -- the ADR-012 Decision 1 ordered startup sequence) and the readiness half
/// of bs-008: one test per named failure mode, each asserting what is disabled AND what still
/// works, plus the ordering guarantee that is the actual bug bs-bw4 describes (remotes must be
/// provisioned before the first deep probe). Every dependency is fake (CLAUDE.md worktree-safety
/// rules): a real temp-directory <c>hosts.toml</c> exercises the real config-loading path (the
/// same accepted pattern <c>BosunHostFactoryTests</c> uses), but nothing here spawns a real
/// process, calls a real rc HTTP endpoint, touches WinFsp, or mounts a real drive.
/// </summary>
public sealed class StartupOrchestratorTests
{
    [Fact]
    public async Task NoConfig_IsFirstRun_NotAFailure_AndEverythingElseStillStarts()
    {
        await using var harness = new Harness(initialConfigContent: null);

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.AwaitingFirstRun, readiness.ConfigState);
        Assert.Empty(readiness.ConfigErrors);
        Assert.True(File.Exists(harness.ConfigPath), "a first-run template should have been written");

        // Everything else still runs: WinFsp detected, rcd started and healthy, supervisor
        // running (with zero hosts, since the template configures none).
        Assert.True(readiness.WinFspInstalled);
        Assert.True(readiness.RcloneHealthy);
        Assert.True(readiness.SupervisorRunning);
        Assert.Single(harness.Launcher.StartCalls);

        // bs-3lt: AwaitingFirstRun is still a valid (empty) config -- the fragment gets written too,
        // even with zero hosts, not just Loaded.
        Assert.True(readiness.TerminalFragmentWritten);
        Assert.NotEmpty(harness.FragmentFileSystem.DestinationContent(harness.FragmentPath) ?? "");
    }

    [Fact]
    public async Task NoConfig_TemplateParsesToZeroHostsAndMatchesGlobalConfigDefaults()
    {
        await using var harness = new Harness(initialConfigContent: null);
        await harness.StartAsync();

        var text = File.ReadAllText(harness.ConfigPath);
        var parsed = ConfigParser.Parse(text, "first-run-template");
        var validation = ConfigValidator.Validate(parsed, _ => true);

        Assert.True(validation.IsValid);
        Assert.Empty(parsed.Hosts);
        Assert.Equal(GlobalConfig.DefaultRcloneRcPort, parsed.Global.RcloneRcPort);
        Assert.Equal(GlobalConfig.DefaultFailuresBeforeUnmount, parsed.Global.FailuresBeforeUnmount);
        Assert.Equal(GlobalConfig.DefaultMountedProbeIntervalSeconds, parsed.Global.MountedProbeIntervalSeconds);
    }

    [Fact]
    public async Task Fragment_IsWrittenBeforeWinFspDetection_AndBeforeRclone()
    {
        // bs-3lt: the fragment write must happen immediately after config loads, strictly before
        // WinFsp detection or rclone -- Terminal profiles must not wait on, or be skipped by,
        // anything downstream that can fail (ADR-012's degradation model). Proven directly rather
        // than inferred: the winFspReader callback below captures whether the fragment was already
        // written by the time WinFsp detection actually runs.
        var fragmentFileSystem = new FakeFragmentFileSystem();
        var fragmentWrittenBeforeWinFspRan = false;
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            fragmentFileSystem: fragmentFileSystem,
            winFspReader: () =>
            {
                fragmentWrittenBeforeWinFspRan = fragmentFileSystem.TouchedPaths.Count > 0;
                return @"C:\WinFsp";
            });

        await harness.StartAsync();

        Assert.True(fragmentWrittenBeforeWinFspRan, "the fragment should already be written by the time WinFsp detection runs");
        Assert.True(harness.Orchestrator.Current.TerminalFragmentWritten);
        Assert.Null(harness.Orchestrator.Current.TerminalFragmentFaultMessage);
        Assert.Contains("nas", fragmentFileSystem.DestinationContent(harness.FragmentPath));
    }

    [Fact]
    public async Task FragmentWriteFailure_IsRecordedCausally_ButDoesNotAbortStartupOrDisableMounting()
    {
        // bs-3lt: "a fragment-write failure is its own degradation ... must not abort startup, and
        // must not disable mounting" -- proven here by driving the exact same persistent-mount,
        // WinFsp-and-rclone-healthy path RemotesAreProvisionedBeforeTheFirstDeepProbe/other happy
        // tests use, but with the fragment write forced to fail, and asserting the mount still
        // happens.
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            failFragmentWriteWith: new IOException("disk full"));

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.False(readiness.TerminalFragmentWritten);
        Assert.Contains("disk full", readiness.TerminalFragmentFaultMessage, StringComparison.Ordinal);

        // Everything else -- WinFsp, rclone, provisioning, the supervisor, and the actual mount --
        // is completely unaffected.
        Assert.True(readiness.WinFspInstalled);
        Assert.True(readiness.RcloneHealthy);
        Assert.True(readiness.SupervisorRunning);
        Assert.True(readiness.MountingAvailable);
        Assert.Contains("nas", harness.Provisioner.EnsureRemoteCalls);
        Assert.Single(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Mounted, snapshot.State);
    }

    [Fact]
    public async Task InvalidConfig_DisablesEverythingDownstream_ButWinFspIsStillReportedAccurately()
    {
        const string malformed = "this is not valid toml [[[";
        await using var harness = new Harness(initialConfigContent: malformed, winFspReader: () => @"C:\WinFsp");

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.Invalid, readiness.ConfigState);
        Assert.NotEmpty(readiness.ConfigErrors);

        // Disabled: nothing that needs a GlobalConfig ran.
        Assert.False(readiness.RcloneHealthy);
        Assert.False(readiness.SupervisorRunning);
        Assert.Empty(harness.Launcher.StartCalls);
        Assert.Empty(harness.Provisioner.EnsureRemoteCalls);

        // Still works: WinFsp detection is independent of config and is reported accurately.
        Assert.True(readiness.WinFspInstalled);
        Assert.Contains("WinFsp", readiness.WinFspMessage, StringComparison.Ordinal);

        Assert.False(readiness.MountingAvailable);
        Assert.Equal("the configuration file is invalid", readiness.MountBlockedReason("anything"));

        // bs-3lt: nothing to write a fragment FROM -- there is no config, valid or otherwise (this
        // is distinct from AwaitingFirstRun, where the template itself is a valid, empty config).
        Assert.False(readiness.TerminalFragmentWritten);
        Assert.Empty(harness.FragmentFileSystem.TouchedPaths);
    }

    [Fact]
    public async Task NoWinFsp_DisablesMounting_ButConfigRcloneAndSupervisorStillRun()
    {
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            winFspReader: () => null);

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.Loaded, readiness.ConfigState);
        Assert.False(readiness.WinFspInstalled);
        Assert.Contains("WinFsp is not installed", readiness.WinFspMessage, StringComparison.Ordinal);

        // Disabled: mounting specifically.
        Assert.False(readiness.MountingAvailable);
        Assert.Equal("WinFsp is not installed", readiness.MountBlockedReason("nas"));

        // Still works: rcd started, the remote was still provisioned, the supervisor is running.
        Assert.True(readiness.RcloneHealthy);
        Assert.Empty(readiness.ProvisioningFailuresByHost);
        Assert.Contains("nas", harness.Provisioner.EnsureRemoteCalls);
        Assert.True(readiness.SupervisorRunning);

        // bs-yvw.1: this is the actual gap. Without a gate on MountSupervisor, "nas" would pass
        // its shallow probe (Ready), pass its deep probe (SFTP operations/list needs neither
        // WinFsp nor rclone -- RecordingProbe/FakeProbe default to Success), and call mount/mount,
        // which would fail and retry forever. The host must reach Ready and stay there, and
        // mount/mount must never be called at all -- asserted on the call count, not on eventual
        // state, per the acceptance criterion.
        Assert.Empty(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Ready, snapshot.State);
        Assert.Equal("WinFsp is not installed", snapshot.MountUnavailableReason);

        // bs-3lt: the fragment write does not depend on WinFsp at all -- Terminal profiles are
        // exactly the thing that must still work when mounting cannot.
        Assert.True(readiness.TerminalFragmentWritten);
        Assert.Contains("nas", harness.FragmentFileSystem.DestinationContent(harness.FragmentPath));
    }

    [Fact]
    public async Task NoRcloneBinary_DisablesMounting_ButConfigAndWinFspStillRun()
    {
        await using var harness = new Harness(initialConfigContent: ValidConfig());
        harness.Launcher.EnqueueExecutableNotFound();

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.Loaded, readiness.ConfigState);
        Assert.True(readiness.WinFspInstalled);

        Assert.False(readiness.RcloneHealthy);
        Assert.Contains("PATH", readiness.RcloneFaultMessage, StringComparison.Ordinal);
        Assert.Empty(readiness.ProvisioningFailuresByHost);
        Assert.Empty(harness.Provisioner.EnsureRemoteCalls);

        // Still works: the supervisor still starts (shallow/idle reachability probing needs
        // neither rclone nor WinFsp) even though nothing can mount.
        Assert.True(readiness.SupervisorRunning);
        Assert.False(readiness.MountingAvailable);

        // bs-3lt: the fragment write does not depend on rclone either -- it runs before rclone is
        // even started.
        Assert.True(readiness.TerminalFragmentWritten);
    }

    [Fact]
    public async Task RcdFailsHealthCheck_DisablesMounting_ButConfigAndWinFspStillRun()
    {
        // pollInterval > timeout: the first failed poll already exceeds the deadline, so no
        // FakeTimeProvider.Advance() is needed -- same trick RcloneProcessServiceTests uses.
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            healthCheckTimeout: TimeSpan.FromMilliseconds(50),
            healthCheckPollInterval: TimeSpan.FromSeconds(5));
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        harness.RcloneClient.EnqueueVersionFailure(new InvalidOperationException("rcd not answering"));

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.False(readiness.RcloneHealthy);
        Assert.NotNull(readiness.RcloneFaultMessage);
        Assert.True(readiness.SupervisorRunning);
        Assert.False(readiness.MountingAvailable);

        // bs-yvw.1: the gate generalises beyond WinFsp -- rcd being unhealthy is the same
        // "mounting cannot possibly succeed" condition, and must block mount/mount identically.
        Assert.Empty(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Ready, snapshot.State);
        Assert.Equal(readiness.RcloneFaultMessage, snapshot.MountUnavailableReason);
    }

    [Fact]
    public async Task RcloneRecoveringAtRuntime_MountsAPreviouslyBlockedPersistentHostWithoutARestart()
    {
        // bs-yvw.1 acceptance: "when mounting becomes available (rcd recovers), the host mounts
        // without needing a restart." The first launch attempt fails (LaunchFailed retries
        // automatically), so the initial sequence completes with rclone unhealthy and the
        // persistent host blocked at Ready; the internal retry then succeeds.
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            restartDelay: TimeSpan.FromSeconds(5));
        harness.Launcher.EnqueueLaunchFailure();

        await harness.StartAsync();
        Assert.False(harness.Orchestrator.Current.RcloneHealthy);
        Assert.Empty(harness.RcloneClient.MountCalls);
        Assert.Equal(MountState.Ready, harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas").State);

        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        await AdvanceUntilAsync(harness.RcloneTime, TimeSpan.FromSeconds(5), () => harness.Orchestrator.Current.RcloneHealthy);

        // The supervisor's own channel processes the recovery asynchronously (it is driven by
        // SupervisorTime/its own loop task, not RcloneTime), so poll briefly for the mount to land
        // rather than asserting the instant RcloneHealthy flips.
        await AdvanceUntilAsync(harness.SupervisorTime, TimeSpan.FromSeconds(1), () => harness.RcloneClient.MountCalls.Count > 0);

        Assert.Single(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Null(snapshot.MountUnavailableReason);
    }

    [Fact]
    public async Task ProvisioningFailsForOneHostButNotOthers()
    {
        var hostA = HostBlock("host-a", MountMode.None);
        var hostB = HostBlock("host-b", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [hostA, hostB]));
        harness.Provisioner.FailFor("host-b", new InvalidOperationException("boom for host-b"));

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.True(readiness.RcloneHealthy);

        // host-b failed, host-a did not -- and the loop kept going past the failure.
        Assert.Contains("host-a", harness.Provisioner.EnsureRemoteCalls);
        Assert.Contains("host-b", harness.Provisioner.EnsureRemoteCalls);

        Assert.True(readiness.ProvisioningFailuresByHost.TryGetValue("host-b", out var message));
        Assert.Contains("boom for host-b", message, StringComparison.Ordinal);
        Assert.False(readiness.ProvisioningFailuresByHost.ContainsKey("host-a"));

        Assert.Equal("boom for host-b", readiness.MountBlockedReason("host-b"));
        Assert.Null(readiness.MountBlockedReason("host-a"));
    }

    [Fact]
    public async Task RemotesAreProvisionedBeforeTheFirstDeepProbe()
    {
        // bs-bw4: this is the actual bug. A persistent host auto-mounts on becoming Ready, which
        // triggers a deep probe against its rclone remote -- if provisioning ran after that (or
        // not at all), the remote would not exist yet and the probe/mount would fail on a real
        // machine, invisibly, because the default test suite fakes IRcloneClient.
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));

        await harness.StartAsync();

        var provisionIndex = harness.Order.IndexOf("provision:nas");
        var deepProbeIndex = harness.Order.IndexOf("deep-probe:nas");

        Assert.True(provisionIndex >= 0, "the remote should have been provisioned");
        Assert.True(deepProbeIndex >= 0, "a deep probe should have run (persistent host auto-mount)");
        Assert.True(
            provisionIndex < deepProbeIndex,
            $"expected provisioning before the first deep probe; order was: {string.Join(", ", harness.Order)}");

        // And the mount actually happened -- proving the ordering wasn't accidentally irrelevant.
        Assert.Single(harness.RcloneClient.MountCalls);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutHangingAfterAFullStart()
    {
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));

        await harness.StartAsync();
        await harness.StopAsync();

        Assert.Equal(RcloneProcessStatus.Stopped, harness.Services.GetRequiredService<RcloneProcessService>().Status);
    }

    [Fact]
    public async Task RcloneBecomingHealthyAgainLater_ReProvisionsAndReconcilesTheSupervisor()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            restartDelay: TimeSpan.FromSeconds(5));

        // First attempt fails (non-terminal -- LaunchFailed retries automatically), so the
        // initial sequence completes with rclone unhealthy and provisioning skipped.
        harness.Launcher.EnqueueLaunchFailure();

        await harness.StartAsync();
        Assert.False(harness.Orchestrator.Current.RcloneHealthy);
        Assert.Empty(harness.Provisioner.EnsureRemoteCalls);

        // Now let the internal retry succeed.
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        await AdvanceUntilAsync(harness.RcloneTime, TimeSpan.FromSeconds(5), () => harness.Orchestrator.Current.RcloneHealthy);

        Assert.True(harness.Orchestrator.Current.RcloneHealthy);
        Assert.Contains("nas", harness.Provisioner.EnsureRemoteCalls);
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            time.Advance(step);
            await Task.Delay(5).ConfigureAwait(false);
        }

        Assert.True(condition(), "condition was not reached within the deadline");
    }

    // identity_file is a literal, non-existent path everywhere here -- the harness registers
    // IHostConfigStore with identityFileExists: _ => true, so no test needs a real file on disk
    // for it (simpler than threading a real per-test path through every TOML fixture, and no less
    // faithful: ConfigValidator's existence check is HostConfigStoreTests'/ConfigValidatorTests'
    // job, not this file's).
    private static string HostBlock(string key, MountMode mode, string drive = "P:") => mode switch
    {
        MountMode.None => $"""
            [hosts.{key}]
            display_name  = "{key}"
            hostname      = "{key}.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = "/fake/identity"

              [hosts.{key}.mount]
              mode = "none"

              [hosts.{key}.session]
              autostart = false
              reconnect = false
              tmux      = false
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.{key}.probe]
              interval_seconds = 0
              deep_probe       = false
            """,
        MountMode.Persistent => $"""
            [hosts.{key}]
            display_name  = "{key}"
            hostname      = "{key}.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = "/fake/identity"

              [hosts.{key}.mount]
              mode                 = "persistent"
              drive                = "{drive}"
              remote_path          = "/srv/share"
              vfs_cache_mode       = "writes"
              network_mode         = true
              idle_unmount_seconds = 0

              [hosts.{key}.session]
              autostart = false
              reconnect = false
              tmux      = false
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.{key}.probe]
              interval_seconds = 60
              deep_probe       = true
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string ValidConfig(params string[] hostBlocks) =>
        "[global]\n\n" + string.Join("\n\n", hostBlocks);

    /// <summary>Decorates <see cref="FakeProbe"/> to log deep-probe calls into a shared,
    /// cross-fake ordering log -- see <see cref="RemotesAreProvisionedBeforeTheFirstDeepProbe"/>.</summary>
    private sealed class RecordingProbe(IProbe inner, List<string> order) : IProbe
    {
        public Task<ShallowProbeResult> ProbeShallowAsync(string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            inner.ProbeShallowAsync(hostname, port, timeout, cancellationToken);

        public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
        {
            order.Add($"deep-probe:{hostKey}");
            return inner.ProbeDeepAsync(hostKey, timeout, cancellationToken);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Root { get; }
        public string ConfigPath { get; }
        public string FragmentPath { get; }
        public FakeRcloneProcessLauncher Launcher { get; } = new();
        public FakeRcloneClient RcloneClient { get; } = new();
        public FakeProbe Probe { get; } = new();
        public FakeRcloneRemoteProvisioner Provisioner { get; }
        public FakeFragmentFileSystem FragmentFileSystem { get; }
        public FakeTimeProvider RcloneTime { get; } = new();
        public FakeTimeProvider SupervisorTime { get; } = new();
        public List<string> Order { get; } = [];
        public ServiceProvider Services { get; }
        public StartupOrchestrator Orchestrator { get; }

        /// <summary>Convenience accessor for bs-yvw.1 assertions -- the same singleton
        /// <see cref="StartupOrchestrator"/> resolves and drives internally.</summary>
        public MountSupervisor MountSupervisor => Services.GetRequiredService<MountSupervisor>();

        public Harness(
            string? initialConfigContent,
            Func<string?>? winFspReader = null,
            TimeSpan? healthCheckTimeout = null,
            TimeSpan? healthCheckPollInterval = null,
            TimeSpan? restartDelay = null,
            Exception? failFragmentWriteWith = null,
            FakeFragmentFileSystem? fragmentFileSystem = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "bosun-tests", "orchestrator", Guid.NewGuid().ToString("N"));
            ConfigPath = Path.Combine(Root, "hosts.toml");
            // A fake path under the test's own temp Root -- never the real
            // %LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json (CLAUDE.md
            // worktree-safety rules; bs-3lt's own acceptance criteria) -- and FakeFragmentFileSystem
            // never touches disk regardless, so this path is never actually created.
            FragmentPath = Path.Combine(Root, "fake-fragments", "bosun.json");
            Provisioner = new FakeRcloneRemoteProvisioner(Order);
            // Accepts a caller-supplied instance so a test can hold a direct reference to it (e.g.
            // to inspect write ordering from inside a winFspReader callback) without going through
            // the not-yet-assigned Harness variable itself.
            FragmentFileSystem = fragmentFileSystem ?? new FakeFragmentFileSystem();

            if (failFragmentWriteWith is not null)
            {
                FragmentFileSystem.FailOnWrite = failFragmentWriteWith;
            }

            if (initialConfigContent is not null)
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(ConfigPath, initialConfigContent);
            }

            var services = new ServiceCollection();
            services.AddLogging();

            // identityFileExists: _ => true -- every HostBlock() fixture uses a literal,
            // non-existent "/fake/identity" path; the real existence check is
            // ConfigValidator's/HostConfigStoreTests' concern, not this file's.
            services.AddSingleton<IHostConfigStore>(sp => HostConfigStore.Load(
                path: ConfigPath,
                reader: new FileConfigReader(),
                watcher: new FileSystemConfigWatcher(ConfigPath),
                timeProvider: TimeProvider.System,
                identityFileExists: _ => true));

            services.AddSingleton<IRcloneClient>(RcloneClient);
            services.AddSingleton<IRcloneProcessLauncher>(Launcher);
            services.AddSingleton(sp => new RcloneProcessService(
                sp.GetRequiredService<IRcloneProcessLauncher>(),
                sp.GetRequiredService<IRcloneClient>(),
                new RcloneProcessServiceOptions
                {
                    RcloneRcPort = 5572,
                    RcloneConfigPath = Path.Combine(Root, "rclone.conf"),
                    HealthCheckTimeout = healthCheckTimeout ?? TimeSpan.FromSeconds(10),
                    HealthCheckPollInterval = healthCheckPollInterval ?? TimeSpan.FromMilliseconds(50),
                    RestartDelay = restartDelay ?? TimeSpan.FromSeconds(5),
                },
                RcloneTime,
                sp.GetRequiredService<ILogger<RcloneProcessService>>(),
                new RcloneRcCredential("bosun-test-user", "bosun-test-pass")));

            services.AddSingleton<IRcloneRemoteProvisioner>(Provisioner);
            services.AddSingleton<IProbe>(new RecordingProbe(Probe, Order));
            services.AddSingleton(sp => new MountSupervisor(
                sp.GetRequiredService<IHostConfigStore>(),
                sp.GetRequiredService<IRcloneClient>(),
                sp.GetRequiredService<IProbe>(),
                SupervisorTime,
                sp.GetRequiredService<ILogger<MountSupervisor>>()));
            services.AddSingleton<IMountSupervisor>(sp => sp.GetRequiredService<MountSupervisor>());

            services.AddSingleton<IFirstRunConfigBootstrapper, FirstRunConfigBootstrapper>();
            services.AddSingleton<IWinFspDetector>(new WinFspDetector(winFspReader ?? (() => @"C:\WinFsp")));

            // bs-3lt: the same fake-filesystem/fake-path pattern FragmentWriterTests /
            // FragmentRewriteCoordinatorTests already use -- FakeFragmentFileSystem never touches a
            // real path regardless of what FragmentPath is set to, so this is safe from a worktree.
            services.AddSingleton<IFragmentFileSystem>(FragmentFileSystem);
            services.AddSingleton(new FragmentWriterOptions { FragmentPath = FragmentPath });
            services.AddSingleton<IFragmentWriter, FragmentWriter>();
            services.AddSingleton(sp => new FragmentRewriteCoordinator(
                sp.GetRequiredService<IFragmentWriter>(),
                sp.GetRequiredService<IHostConfigStore>(),
                sp.GetRequiredService<ILogger<FragmentRewriteCoordinator>>()));

            Services = services.BuildServiceProvider();

            var options = new BosunHostOptions { LogDirectory = Root, ConfigPath = ConfigPath };
            Orchestrator = new StartupOrchestrator(
                Services,
                options,
                Services.GetRequiredService<IFirstRunConfigBootstrapper>(),
                Services.GetRequiredService<IWinFspDetector>(),
                Services.GetRequiredService<ILogger<StartupOrchestrator>>());
        }

        public async Task StartAsync()
        {
            var task = Orchestrator.StartAsync(CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(task, completed), "StartAsync did not complete -- possible deadlock");
            await task;
        }

        public async Task StopAsync()
        {
            var task = Orchestrator.StopAsync(CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(task, completed), "StopAsync did not complete -- possible deadlock");
            await task;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                // StartupOrchestrator.StopAsync is idempotent (a test may have already called it
                // explicitly), so this is genuinely best-effort teardown, not a mask for a real
                // hang -- surface it rather than swallowing silently, so a regression here still
                // shows up somewhere.
                Console.Error.WriteLine($"Harness teardown: StopAsync threw: {ex}");
            }

            await Services.DisposeAsync();

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
