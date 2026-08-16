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
        using var host = BosunHostFactory.CreateHost(new BosunHostOptions { LogDirectory = _logDirectory });

        var logger = host.Services.GetRequiredService<ILogger<BosunHostFactoryTests>>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        Assert.NotNull(logger);
        Assert.NotNull(lifetime);
    }

    [Fact]
    public async Task Host_StartsAndStopsCleanlyWithoutAWindow()
    {
        using var host = BosunHostFactory.CreateHost(new BosunHostOptions { LogDirectory = _logDirectory });

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void CreateHost_CreatesTheInjectedLogDirectory()
    {
        Assert.False(Directory.Exists(_logDirectory));

        using var host = BosunHostFactory.CreateHost(new BosunHostOptions { LogDirectory = _logDirectory });

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
}
