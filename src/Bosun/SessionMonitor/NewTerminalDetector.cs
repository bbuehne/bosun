namespace Bosun.SessionMonitor;

/// <summary>
/// Pure differ behind ADR-021 Decision 7 ("Bosun watches for a new <c>WindowsTerminal.exe</c>").
/// Given the previous and current snapshots, reports Terminal processes that were not present
/// before. The timer that drives it, and the baseline snapshot taken at Bosun start, belong to the
/// caller: with an empty <c>previous</c> every running Terminal is reported as new.
/// </summary>
public static class NewTerminalDetector
{
    public static IReadOnlyList<ProcessSnapshotEntry> FindNewTerminals(
        IReadOnlyCollection<ProcessSnapshotEntry> previous,
        IReadOnlyCollection<ProcessSnapshotEntry> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var before = previous.Where(IsTerminal).ToList();
        return current
            .Where(IsTerminal)
            .Where(c => !before.Any(p => ProcessIdentity.SameProcess(p.Pid, p.CreationTimeUtc, c.Pid, c.CreationTimeUtc)))
            .ToList();
    }

    private static bool IsTerminal(ProcessSnapshotEntry e) =>
        string.Equals(e.ImageName, ClaudeProcessImages.WindowsTerminal, StringComparison.OrdinalIgnoreCase);
}
