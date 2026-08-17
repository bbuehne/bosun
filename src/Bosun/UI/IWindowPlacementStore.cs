namespace Bosun.UI;

/// <summary>
/// Persists <see cref="WindowPlacement"/> across runs. Behind an interface so
/// <see cref="MainWindowController"/> is testable with a fake store, per CLAUDE.md's
/// worktree-safety rules (the real implementation writes a file; tests must never write to the
/// real <c>%LOCALAPPDATA%</c> location).
/// </summary>
public interface IWindowPlacementStore
{
    /// <summary><see langword="null"/> on first run, or if the persisted data could not be read
    /// (missing file, corrupt JSON, permission error) -- never throws.</summary>
    WindowPlacement? TryLoad();

    /// <summary>Best-effort. A failure to persist (permission error, disk full) is logged, never
    /// thrown -- losing the next run's remembered geometry is not worth crashing over.</summary>
    void Save(WindowPlacement placement);
}
