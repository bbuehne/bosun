using Bosun.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers the acceptance criterion for bs-xqs: a test writes to a temp log path and asserts the
/// file is produced and parseable. This is the rolling-file half of logging; the injectable-path
/// half is covered by <see cref="BosunHostFactoryTests"/>.
/// </summary>
public sealed class SerilogFileSinkTests : IDisposable
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
    public async Task LoggedMessage_IsWrittenToTheRollingFileAndIsParseable()
    {
        const string marker = "bs-xqs-sentinel-message";

        var host = BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _logDirectory,
            ConfigPath = Path.Combine(_logDirectory, "hosts.toml"),
        });
        try
        {
            var logger = host.Services.GetRequiredService<ILogger<SerilogFileSinkTests>>();
            logger.LogInformation("{Marker} fired at {Now:O}", marker, DateTimeOffset.UtcNow);
        }
        finally
        {
            // BosunHostFactory registers Serilog with dispose: true, so disposing the host
            // disposes the Serilog logger and flushes the file sink.
            host.Dispose();
        }

        var logFiles = Directory.GetFiles(_logDirectory, "bosun-*.log");
        Assert.Single(logFiles);

        var contents = await File.ReadAllTextAsync(logFiles[0]);

        Assert.False(string.IsNullOrWhiteSpace(contents));
        Assert.Contains(marker, contents);
        Assert.Contains("[INF]", contents);
    }
}
