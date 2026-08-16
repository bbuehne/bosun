using System.IO;

namespace Bosun.Hosting;

/// <summary>
/// Options controlling how the Bosun <see cref="Microsoft.Extensions.Hosting.IHost"/> is
/// constructed. The log directory is always explicit, never hardcoded inside
/// <see cref="BosunHostFactory"/>, so tests can point it at a temp directory and never write to
/// the real <c>%LOCALAPPDATA%</c> location.
/// </summary>
public sealed record BosunHostOptions
{
    /// <summary>
    /// Directory the rolling daily log file is written into. Created if it does not exist.
    /// </summary>
    public required string LogDirectory { get; init; }

    /// <summary>
    /// Path to <c>hosts.toml</c>, read by the registered <c>IHostConfigStore</c>. Always
    /// explicit for the same reason as <see cref="LogDirectory"/>: tests must be able to point
    /// it at a fixture, never the maintainer's real, gitignored <c>config/hosts.toml</c>.
    /// </summary>
    /// <remarks>
    /// The default below (next to the executable, mirroring this repo's own
    /// <c>config/hosts.toml</c> layout) is an assumption, not a confirmed convention —
    /// docs/OPERATIONS.md does not pin down where an installed copy of Bosun should look for its
    /// config. Flagged as bs-worth-confirming; see the bs-0na/bs-c0g/bs-30b delivery report.
    /// </remarks>
    public required string ConfigPath { get; init; }

    /// <summary>
    /// The options Bosun uses at real runtime: logs under <c>%LOCALAPPDATA%\Bosun\logs</c>
    /// (docs/OPERATIONS.md "Logs"), config at <c>&lt;app directory&gt;\config\hosts.toml</c>.
    /// </summary>
    public static BosunHostOptions CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new BosunHostOptions
        {
            LogDirectory = Path.Combine(localAppData, "Bosun", "logs"),
            ConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "hosts.toml"),
        };
    }
}
