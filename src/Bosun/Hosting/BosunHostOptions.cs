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
    /// ADR-012 Decision 4: <c>%LOCALAPPDATA%\Bosun\hosts.toml</c>, symmetric with
    /// <see cref="LogDirectory"/> so docs/OPERATIONS.md can say "your configuration and your logs
    /// are both under <c>%LOCALAPPDATA%\Bosun</c>". A repo-relative path (for local development
    /// against this repo's own <c>config/hosts.toml</c>) survives only as a deliberate override —
    /// construct a <see cref="BosunHostOptions"/> directly rather than using this default.
    /// </remarks>
    public required string ConfigPath { get; init; }

    /// <summary>
    /// The options Bosun uses at real runtime: both logs and config under
    /// <c>%LOCALAPPDATA%\Bosun</c> (ADR-012 Decision 4; docs/OPERATIONS.md "Logs" and
    /// "Configuration").
    /// </summary>
    public static BosunHostOptions CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bosunDirectory = Path.Combine(localAppData, "Bosun");
        return new BosunHostOptions
        {
            LogDirectory = Path.Combine(bosunDirectory, "logs"),
            ConfigPath = Path.Combine(bosunDirectory, "hosts.toml"),
        };
    }
}
