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
/// text that may be reworded freely. A <see cref="MountState.Mounted"/> -&gt;
/// <see cref="MountState.Draining"/> transition is "unexpected" unless its cause is
/// <see cref="DrainCause.UserUnmount"/> or <see cref="DrainCause.Suspend"/>, the two causes where
/// the user or the OS asked for the unmount. Every other cause (a probe-failure threshold, a
/// deep-probe failure, reconciliation drift, an idle timeout, a config change, a repair, a mount
/// failure) is an established mount going away without the user having just asked for that
/// specific outcome, which is the case ADR-005 says must be communicated. A transition with no
/// recorded cause cannot come from the supervisor but is treated as unexpected: failing toward a
/// balloon is the safe direction for a rule that "MUST NOT be silent".
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

        return entry.DrainCause is not (DrainCause.UserUnmount or DrainCause.Suspend);
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
