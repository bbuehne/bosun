using Bosun.Configuration;
using Bosun.Hosting;
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
        using var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            // Never resolved in these tests (IHostConfigStore is a lazy DI registration), so
            // this need not point at a real file -- see CreateHost_ResolvesIHostConfigStore_WhenConfigPathPointsAtAValidFixture.
            ConfigPath = Path.Combine(_logDirectory, "hosts.toml"),
        });

        await host.StartAsync();
        await host.StopAsync();
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
    public void CreateDefault_PointsConfigPathUnderAppDirectoryConfigHostsToml()
    {
        // Same rationale as CreateDefault_PointsUnderLocalApplicationDataBosunLogs: computes a
        // path string only, never passed to CreateHost from a test.
        var options = BosunHostOptions.CreateDefault();

        var expected = Path.Combine(AppContext.BaseDirectory, "config", "hosts.toml");

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
}
