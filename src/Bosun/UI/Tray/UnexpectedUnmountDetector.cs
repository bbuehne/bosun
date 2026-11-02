using Bosun.Supervisor;

namespace Bosun.UI.Tray;

/// <summary>
/// Picks out "unexpected unmount" transitions from <c>IMountSupervisor.GetTransitionHistory()</c>
/// -- the user-visible consequence of ADR-005 that bs-ww9.5 requires a balloon for ("This is the
/// user-visible consequence of ADR-005 ... and it MUST NOT be silent").
/// </summary>
/// <remarks>
/// <b>How "unexpected" is decided.</b> By <see cref="MountTransitionEntry.DrainCause"/>, a typed
/// value the supervisor records on every transition into <see cref="MountState.Draining"/>
/// (bs-ynb) -- never by reading <see cref="MountTransitionEntry.Trigger"/>, which is human-readable
/// text that may be reworded freely.
/// <para>
/// The maintainer's decision (bs-p97, 2026-11-01): the toast is for a genuinely unexpected loss of
/// a <see cref="MountState.Mounted"/> drive and nothing else. A <see cref="MountState.Mounted"/>
/// -&gt; <see cref="MountState.Draining"/> transition therefore toasts only for
/// <see cref="DrainCause.Automatic"/> (a shallow or deep probe failure, or reconciliation drift)
/// or for a missing cause, which cannot come from the supervisor but fails toward a balloon
/// because ADR-005 says an unexpected unmount "MUST NOT be silent". Every other cause is silent
/// here and is still logged and shown in the window: <see cref="DrainCause.UserUnmount"/>,
/// <see cref="DrainCause.Suspend"/>, <see cref="DrainCause.IdleTimeout"/>,
/// <see cref="DrainCause.Repair"/> (the user's own "Unmount all and re-probe"),
/// <see cref="DrainCause.ConfigChanged"/> and <see cref="DrainCause.ConfigRemoved"/> (the user's
/// own editor action), and <see cref="DrainCause.MountFailure"/> (an attempt that never reached
/// <see cref="MountState.Mounted"/>; the supervisor only records it from
/// <see cref="MountState.Mounting"/>, so it is also excluded by the From check).
/// </para>
/// <para>
/// <b>Restart rclone (bs-z9e).</b> The reconciliation after a user "Restart rclone" is
/// <see cref="DrainCause.Repair"/>, because <c>RepairCommands</c> tells the supervisor first
/// (<c>IMountSupervisor.NoteUserRequestedRcloneRestart</c>). An rcd crash restart is not announced and
/// stays <see cref="DrainCause.Automatic"/>, so it still toasts.
/// </para>
/// </remarks>
public static class UnexpectedUnmountDetector
{
    /// <summary>True if <paramref name="entry"/> represents a host falling out of
    /// <see cref="MountState.Mounted"/> for a reason other than the user or the OS deliberately
    /// asking for it.</summary>
    public static bool IsUnexpectedUnmount(MountTransitionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.From != MountState.Mounted || entry.To != MountState.Draining)
        {
            return false;
        }

        return entry.DrainCause is null or DrainCause.Automatic;
    }

    /// <summary>
    /// Selects the unexpected-unmount entries newer than <paramref name="since"/> out of
    /// <paramref name="newestFirstHistory"/>, returned OLDEST-first (a sane order to raise
    /// notifications in, even though the source history is newest-first per
    /// <c>IMountSupervisor.GetTransitionHistory</c>'s own contract).
    /// </summary>
    /// <remarks>
    /// Relies on the history being newest-first and stops at the first entry whose timestamp is
    /// not newer than <paramref name="since"/> -- correct as long as
    /// <see cref="MountTransitionEntry.TimestampUtc"/> values are non-decreasing as new entries are
    /// appended, which is the supervisor's own contract (its injected <c>TimeProvider</c>, never
    /// wall-clock time directly).
    /// </remarks>
    public static IReadOnlyList<MountTransitionEntry> SelectNewUnexpectedUnmounts(
        IReadOnlyList<MountTransitionEntry> newestFirstHistory, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(newestFirstHistory);

        var result = new List<MountTransitionEntry>();

        foreach (var entry in newestFirstHistory)
        {
            if (entry.TimestampUtc <= since)
            {
                break;
            }

            if (IsUnexpectedUnmount(entry))
            {
                result.Add(entry);
            }
        }

        result.Reverse();
        return result;
    }
}
