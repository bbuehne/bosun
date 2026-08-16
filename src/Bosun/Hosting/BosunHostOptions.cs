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
    /// The options Bosun uses at real runtime: <c>%LOCALAPPDATA%\Bosun\logs</c>.
    /// See docs/OPERATIONS.md "Logs".
    /// </summary>
    public static BosunHostOptions CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new BosunHostOptions
        {
            LogDirectory = Path.Combine(localAppData, "Bosun", "logs"),
        };
    }
}
