using Bosun.Configuration;
using Bosun.Hosting;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.SessionMonitor;
using Bosun.Supervisor;
using Bosun.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers bs-t78 (host builds, starts, stops, resolves registrations) and the log-path half of
/// bs-xqs. Every host here is built with an injected temp directory — never the real
/// <c>%LOCALAPPDATA%\Bosun\logs</c> — per CLAUDE.md's worktree-safety rules.
/// </summary>
public sealed class BosunHostFactoryTests : IDisposable
{
    private readonly string _logDirectory =
        Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateHost_ResolvesLoggerAndLifetimeRegistrations()
    {
        using var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            // Never resolved in these tests (IHostConfigStore is a lazy DI registration), so
            // this need not point at a real file -- see CreateHost_ResolvesIHostConfigStore_WhenConfigPathPointsAtAValidFixture.
            ConfigPath = Path.Combine(_logDirectory, "hosts.toml"),
        });

        var logger = host.Services.GetRequiredService<ILogger<BosunHostFactoryTests>>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        Assert.NotNull(logger);
        Assert.NotNull(lifetime);
    }

    [Fact]
    public async Task Host_StartsAndStopsCleanlyWithoutAWindow()
    {
        // registerStartupOrchestrator: false -- per ADR-012's "fix the test, not the
        // architecture" note (bs-127): in production, host.StartAsync() now runs
        // StartupOrchestrator, which loads config for real, spawns a real `rclone` child process,
        // and can drive a real drive letter. None of that is safe from a default-suite test
        // (CLAUDE.md worktree-safety rules), so this test builds a host WITHOUT the orchestrator
        // registered -- it exists to prove the HOST ITSELF (DI container, logging, lifetime)
        // starts and stops cleanly, not to exercise the startup sequence (that is
        // StartupOrchestratorTests' job).
        using var host = BosunHostFactory.CreateHost(
            new BosunHostOptions
            {
                LogDirectory = _logDirectory,
                // Never resolved in these tests (IHostConfigStore is a lazy DI registration), so
                // this need not point at a real file -- see CreateHost_ResolvesIHostConfigStore_WhenConfigPathPointsAtAValidFixture.
                ConfigPath = Path.Combine(_logDirectory, "hosts.toml"),
            },
            registerStartupOrchestrator: false);

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Host_WithStartupOrchestratorRegistered_DoesNotResolveIHostConfigStoreOrStartAnythingUntilStarted()
    {
        // The default (registerStartupOrchestrator: true) is safe to BUILD from a worktree --
        // nothing eager happens at Build() time, only when the host is actually started, which
        // this test deliberately does not do. Covers the other half of the bs-127 contract: the
        // flag defaults to production behaviour, it doesn't silently disable the orchestrator.
        using var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            ConfigPath = Path.Combine(_logDirectory, "hosts.toml"),
        });

        var orchestrator = host.Services.GetRequiredService<Bosun.Hosting.StartupOrchestrator>();

        Assert.Equal(Bosun.Hosting.ConfigReadinessState.Invalid, orchestrator.Current.ConfigState);
        await Task.CompletedTask;
    }

    [Fact]
    public void CreateHost_CreatesTheInjectedLogDirectory()
    {
        Assert.False(Directory.Exists(_logDirectory));

        using var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            // Never resolved in these tests (IHostConfigStore is a lazy DI registration), so
            // this need not point at a real file -- see CreateHost_ResolvesIHostConfigStore_WhenConfigPathPointsAtAValidFixture.
            ConfigPath = Path.Combine(_logDirectory, "hosts.toml"),
        });

        Assert.True(Directory.Exists(_logDirectory));
    }

    [Fact]
    public void CreateDefault_PointsUnderLocalApplicationDataBosunLogs()
    {
        // This only computes a path string; it must never be passed to CreateHost from a test,
        // which would create the real directory. See docs/OPERATIONS.md "Logs".
        var options = BosunHostOptions.CreateDefault();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bosun",
            "logs");

        Assert.Equal(expected, options.LogDirectory);
    }

    [Fact]
    public void CreateDefault_PointsConfigPathUnderLocalApplicationDataBosunHostsToml()
    {
        // ADR-012 Decision 4 (bs-008): symmetric with the log path. Same rationale as
        // CreateDefault_PointsUnderLocalApplicationDataBosunLogs: computes a path string only,
        // never passed to CreateHost from a test.
        var options = BosunHostOptions.CreateDefault();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bosun",
            "hosts.toml");

        Assert.Equal(expected, options.ConfigPath);
    }

    [Fact]
    public void CreateHost_ResolvesIHostConfigStore_WhenConfigPathPointsAtAValidFixture()
    {
        // A fixture the test owns and writes itself -- never the maintainer's real, gitignored
        // config/hosts.toml (CLAUDE.md worktree-safety rules). This exercises the real
        // FileConfigReader/FileSystemConfigWatcher wiring registered in BosunHostFactory, but
        // only the synchronous startup load: nothing here waits on watcher timing.
        var configPath = Path.Combine(_logDirectory, "hosts.toml");
        Directory.CreateDirectory(_logDirectory);

        // HostConfigStore.Load, as wired in BosunHostFactory, uses the REAL identity-file
        // existence check (File.Exists) -- there is no fake to inject through this path. So the
        // fixture must reference a file this test actually creates, rather than a plausible-
        // looking but non-existent path, or this test's outcome would depend on whatever happens
        // to exist under the real machine's home directory. TOML literal strings (single quotes)
        // avoid having to escape the Windows path's backslashes.
        var identityFilePath = Path.Combine(_logDirectory, "id_ed25519_fixture");
        File.WriteAllText(identityFilePath, "not a real key -- existence is all that's checked");

        File.WriteAllText(configPath, $$"""
            [hosts.fixture-host]
            display_name  = "Fixture Host"
            hostname      = "fixture.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = '{{identityFilePath}}'

              [hosts.fixture-host.mount]
              mode = "none"

              [hosts.fixture-host.session]
              autostart = false
              reconnect = true
              tmux      = false
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.fixture-host.probe]
              interval_seconds = 0
              deep_probe       = false
            """);

        using var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            ConfigPath = configPath,
        });

        var store = host.Services.GetRequiredService<IHostConfigStore>();

        Assert.True(store.Current.Hosts.ContainsKey("fixture-host"));
    }

    /// <summary>
    /// Resolves the services the running application actually asks for, which the other tests in
    /// this class did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Bosun shipped with <see cref="TimeProvider"/> unregistered.
    /// <c>HostProbe</c> is registered BY TYPE, so the container constructs it and needs every
    /// constructor parameter resolvable; without that registration <c>IProbe</c> could not be
    /// built, <c>MountSupervisor</c> could not be built with it, and the whole supervisor failed
    /// to start. The application launched, wrote its Terminal fragment, brought up
    /// <c>rclone rcd</c>, logged "Failed to start the mount supervisor; no host will be probed or
    /// mounted this session", and then sat there doing nothing.
    /// </para>
    /// <para>
    /// <b>Why the existing tests did not catch it, which is the more useful lesson.</b>
    /// <c>Host_StartsAndStopsCleanlyWithoutAWindow</c> starts the host and passes -- because
    /// <c>StartupOrchestrator</c> deliberately CATCHES a supervisor start failure and logs it,
    /// per ADR-012's fail-soft contract. "The host started cleanly" is therefore not evidence
    /// that anything inside it works. The fail-soft behaviour that makes Bosun robust in
    /// production is precisely what made the test suite blind here, so the fix is not to weaken
    /// the fail-soft: it is to resolve the graph explicitly, which is what this does.
    /// </para>
    /// <para>
    /// Deliberately resolves rather than merely asserting registration: a service can be
    /// registered and still be unconstructible, which is exactly the bug. Every type here is inert
    /// to construct -- no probe is issued, no process spawned, no mount attempted -- so this stays
    /// safe to run from a worktree.
    /// </para>
    /// </remarks>
    [Fact]
    public void CreateHost_ResolvesTheServicesTheApplicationActuallyUses()
    {
        var configPath = Path.Combine(_logDirectory, "hosts.toml");
        Directory.CreateDirectory(_logDirectory);
        File.WriteAllText(configPath, "# no hosts; this test is about the container, not the config");

        using var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            ConfigPath = configPath,
        });

        // The one that actually broke, and the graph it drags in (IProbe -> TimeProvider).
        Assert.NotNull(host.Services.GetRequiredService<IMountSupervisor>());
        Assert.NotNull(host.Services.GetRequiredService<MountSupervisor>());
        Assert.NotNull(host.Services.GetRequiredService<IProbe>());
        Assert.NotNull(host.Services.GetRequiredService<TimeProvider>());

        // The rest of what App.InitializeUserInterface and the hosted services reach for.
        Assert.NotNull(host.Services.GetRequiredService<IHostConfigStore>());
        Assert.NotNull(host.Services.GetRequiredService<ISessionMonitor>());
        Assert.NotNull(host.Services.GetRequiredService<IRcloneClient>());
        Assert.NotNull(host.Services.GetRequiredService<IFragmentWriter>());
        Assert.NotNull(host.Services.GetRequiredService<IRemoteRootLister>());
        Assert.NotNull(host.Services.GetRequiredService<IWinFspDetector>());
    }
}
