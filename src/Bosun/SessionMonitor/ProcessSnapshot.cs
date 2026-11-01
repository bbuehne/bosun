namespace Bosun.SessionMonitor;

/// <summary>
/// A Windows logon session as ADR-021 Decision 5 needs it: the LUID (<c>TOKEN_STATISTICS.AuthenticationId</c>)
/// plus the logon time <c>LsaGetLogonSessionData</c> reports for it. The ID alone is enough to tell
/// "same session" within one boot, but LUIDs are handed out per boot and reused across them in
/// principle; the pair is what the record stores and what row 5 compares.
/// </summary>
/// <param name="Luid">The LUID as a 64-bit value (<c>HighPart</c> in the top 32 bits).</param>
/// <param name="LogonTimeUtc">UTC logon time at full 100 ns precision, or <see langword="null"/> when
/// the LSA refused the lookup (the session belongs to another user). Value equality includes this
/// field, so a session with a known time never equals the same LUID with an unknown one; callers
/// comparing across a possible unknown must decide that deliberately.</param>
public readonly record struct LogonSessionId(ulong Luid, DateTime? LogonTimeUtc);

/// <summary>
/// One live process of interest, as seen by <see cref="IProcessSnapshotSource"/> (ADR-021
/// Decision 4). Identity is <see cref="Pid"/> plus <see cref="CreationTimeUtc"/>, never PID alone
/// (Decision 5); compare creation times with <see cref="ProcessIdentity.SameProcess"/>, not
/// <c>==</c>.
/// </summary>
public sealed record ProcessSnapshotEntry
{
    public required int Pid { get; init; }

    /// <summary>UTC creation time at full 100 ns precision, straight from <c>GetProcessTimes</c>.
    /// Never null: a process whose creation time cannot be read cannot be identified and is
    /// omitted from the snapshot instead.</summary>
    public required DateTime CreationTimeUtc { get; init; }

    /// <summary>The requested image file name this process matched, in the casing the caller
    /// requested (for example <c>WindowsTerminal.exe</c>). Needed because <see cref="ImagePath"/>
    /// can be unknown.</summary>
    public required string ImageName { get; init; }

    /// <summary>Full image path, or <see langword="null"/> when it could not be read.</summary>
    public string? ImagePath { get; init; }

    /// <summary>The process's logon session, or <see langword="null"/> when its token could not be
    /// opened (an elevated or other-user process from a medium-integrity Bosun).</summary>
    public LogonSessionId? LogonSession { get; init; }
}

/// <summary>
/// Seam for the Claude Code process snapshot (ADR-021, bs-vqh.7). The real implementation does the
/// Win32 work behind <c>SessionMonitor/Interop/</c>; the classifier and Terminal-launch watcher
/// consume this interface and are tested against fakes.
/// </summary>
public interface IProcessSnapshotSource
{
    /// <summary>
    /// Every live process whose image file name is in <paramref name="imageNames"/>
    /// (case-insensitive, with or without a <c>.exe</c> suffix). Never throws for an unreadable
    /// process: unreadable fields come back <see langword="null"/>, and a process that vanishes
    /// mid-enumeration is omitted.
    /// </summary>
    IReadOnlyList<ProcessSnapshotEntry> Snapshot(IReadOnlyCollection<string> imageNames);

    /// <summary>The logon session of Bosun's own process, or <see langword="null"/> if it could not
    /// be read (not expected in practice: a process can always open its own token).</summary>
    LogonSessionId? CurrentLogonSession { get; }
}

/// <summary>Image names Bosun's Claude Code restore feature (ADR-021) needs in a snapshot.</summary>
public static class ClaudeProcessImages
{
    public const string Claude = "claude.exe";
    public const string Node = "node.exe";
    public const string WindowsTerminal = "WindowsTerminal.exe";

    public static IReadOnlyList<string> All { get; } = [Claude, Node, WindowsTerminal];
}

/// <summary>Pure process-identity helpers (ADR-021 Decision 5).</summary>
public static class ProcessIdentity
{
    /// <summary>Creation times are compared at 1 ms resolution: the helper and Bosun may read them
    /// through different APIs with different precision, and an exact comparison would read a live
    /// session as dead and open a duplicate.</summary>
    public static readonly TimeSpan CreationTimeTolerance = TimeSpan.FromMilliseconds(1);

    public static bool CreationTimesMatch(DateTime a, DateTime b) =>
        Math.Abs((a - b).Ticks) <= CreationTimeTolerance.Ticks;

    public static bool SameProcess(int pidA, DateTime creationA, int pidB, DateTime creationB) =>
        pidA == pidB && CreationTimesMatch(creationA, creationB);
}
