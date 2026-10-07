using System.IO;
using Serilog;
using Serilog.Events;

namespace Bosun.Hosting;

/// <summary>
/// The one place Bosun's log levels and file-sink limits are decided (bs-qcs). Kept apart from
/// <see cref="BosunHostFactory"/> so they can be tested without building a host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Level.</b> Information in production. Debug is opt-in through the
/// <see cref="LevelEnvironmentVariable"/> environment variable, read once at startup (the logger
/// exists before <c>hosts.toml</c> is loaded, and a startup fault is exactly when Debug is wanted,
/// so a config key would be unavailable when it matters). <c>Microsoft.*</c> and <c>System.*</c>
/// stay at Warning at every level.
/// </para>
/// <para>
/// <b>Size.</b> Daily files, each rolled at <see cref="FileSizeLimitBytes"/>, and at most
/// <see cref="RetainedFileCountLimit"/> files kept: a worst case of 200 MB on disk. The
/// Serilog defaults were 1 GB per file and 31 files, and a log flood during an rclone outage once
/// wrote 203 MB a day.
/// </para>
/// </remarks>
public static class BosunLogging
{
    /// <summary>Set to <c>Debug</c> (or <c>Verbose</c>) to log more. Unset means Information.</summary>
    public const string LevelEnvironmentVariable = "BOSUN_LOG_LEVEL";

    public const long FileSizeLimitBytes = 20L * 1024 * 1024;

    /// <summary>With <see cref="FileSizeLimitBytes"/> this keeps the log directory at or below 200 MB.</summary>
    public const int RetainedFileCountLimit = 10;

    /// <summary>
    /// The minimum level for <paramref name="setting"/>: Information when it is missing, blank, or
    /// not a Serilog level name (case-insensitive). <paramref name="problem"/> says what was wrong
    /// with a value that was present but unusable, for the caller to log once the logger exists.
    /// </summary>
    public static LogEventLevel ResolveMinimumLevel(string? setting, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(setting))
        {
            return LogEventLevel.Information;
        }

        // Matched by name, not Enum.TryParse: that also accepts "Debug,Information" (a flags-style
        // OR, which lands on Warning) and bare numbers.
        var name = Enum.GetNames<LogEventLevel>()
            .FirstOrDefault(n => string.Equals(n, setting.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            return Enum.Parse<LogEventLevel>(name);
        }

        problem = $"{LevelEnvironmentVariable}='{setting}' is not a log level " +
                  $"({string.Join(", ", Enum.GetNames<LogEventLevel>())}); using Information";
        return LogEventLevel.Information;
    }

    /// <summary>Applies the minimum level and the framework overrides to <paramref name="configuration"/>.</summary>
    public static LoggerConfiguration ApplyLevels(LoggerConfiguration configuration, LogEventLevel minimum)
    {
        configuration.MinimumLevel.Is(minimum);
        configuration.MinimumLevel.Override("Microsoft", LogEventLevel.Warning);
        configuration.MinimumLevel.Override("System", LogEventLevel.Warning);
        return configuration;
    }

    /// <summary>
    /// The daily rolling file sink, size-capped. Rolled-over files are named
    /// <c>bosun-yyyyMMdd_001.log</c>, <c>_002</c> and so on, so <c>bosun-*.log</c> matches them all.
    /// </summary>
    public static LoggerConfiguration WriteToCappedFile(
        LoggerConfiguration configuration,
        string logDirectory,
        string outputTemplate,
        long fileSizeLimitBytes = FileSizeLimitBytes,
        int retainedFileCountLimit = RetainedFileCountLimit)
    {
        configuration.WriteTo.File(
            Path.Combine(logDirectory, "bosun-.log"),
            outputTemplate: outputTemplate,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: fileSizeLimitBytes,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: retainedFileCountLimit);
        return configuration;
    }
}
