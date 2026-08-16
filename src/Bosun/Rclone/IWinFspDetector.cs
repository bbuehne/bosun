namespace Bosun.Rclone;

/// <summary>
/// Detects whether WinFsp is installed (bs-5mt; ADR-002: "WinFsp is a hard prerequisite and must
/// be detected at startup with an actionable message"). WinFsp is NOT installed on the
/// maintainer's machine as of this epic landing, so <see cref="Detect"/>'s "not installed" path
/// is the one that will actually fire on first run -- getting its message right matters more
/// than the "installed" path.
/// </summary>
public interface IWinFspDetector
{
    WinFspDetectionResult Detect();
}

/// <summary>
/// Result of <see cref="IWinFspDetector.Detect"/>. <see cref="Message"/> is always populated,
/// on both outcomes, so a caller can log or surface it without an extra branch.
/// </summary>
public sealed record WinFspDetectionResult
{
    public required bool IsInstalled { get; init; }

    /// <summary>WinFsp's install directory, when found. Informational only.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>
    /// Human-readable, actionable text: on success, a short confirmation; on failure, what is
    /// missing and what to do about it (docs/ARCHITECTURE.md §6: "Detect at startup, show an
    /// actionable message, disable all mount features, leave terminal features working").
    /// </summary>
    public required string Message { get; init; }
}
