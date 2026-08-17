using Bosun.Supervisor;

namespace Bosun.UI.Tray;

/// <summary>
/// Picks out "unexpected unmount" transitions from <c>IMountSupervisor.GetTransitionHistory()</c>
/// -- the user-visible consequence of ADR-005 that bs-ww9.5 requires a balloon for ("This is the
/// user-visible consequence of ADR-005 ... and it MUST NOT be silent").
/// </summary>
/// <remarks>
/// <para>
/// <b>How "unexpected" is decided, and why this is a heuristic rather than a typed field.</b>
/// <see cref="MountTransitionEntry"/> carries only a free-text <c>Trigger</c> string -- the cause
/// is not a structured, public value. <c>MountSupervisor</c> has an internal <c>DrainCause</c> enum
/// (<c>Automatic</c> / <c>Suspend</c> / <c>UserUnmount</c> / <c>MountFailure</c>) that would make
/// this exact, but it is private and this epic's hard rule is that ONLY <c>IMountSupervisor</c>
/// touches supervisor internals -- extending its public surface is an architecture question, not an
/// implementation detail (see this epic's brief). So this class treats a
/// <see cref="MountState.Mounted"/> -&gt; <see cref="MountState.Draining"/> transition as
/// "unexpected" unless its trigger text starts with one of the two deliberate-cause prefixes
/// <c>MountSupervisor</c> is known to emit verbatim (<c>"user unmount"</c>, <c>"system suspend"</c>
/// -- see its source for <c>RequestUnmountAsync</c>/<c>SuspendAsync</c>). Every other Mounted-&gt;
/// Draining trigger text observed in the supervisor today (a consecutive-probe-failure threshold, a
/// deep-probe failure, reconciliation drift, a mount/mount failure) reads as an unexpected loss of
/// an established mount, which is exactly the case ADR-005 says must be communicated.
/// </para>
/// <para>
/// <b>Flagged as discovered work, not silently accepted as final.</b> String-matching a log
/// message is fragile: a future rewording of either prefix silently turns an expected drain into a
/// balloon, or (worse) the reverse. The durable fix is exposing drain cause as a structured field
/// on <see cref="MountTransitionEntry"/> (or an equivalent public signal) so this class -- and
/// bs-ww9.6's causal status text, which has the identical problem -- can match on a value, not a
/// sentence. That is Supervisor-epic work, out of scope here; see the delivery report.
/// </para>
/// </remarks>
public static class UnexpectedUnmountDetector
{
    /// <summary>Trigger-text prefixes <c>MountSupervisor</c> uses for a DELIBERATE unmount --
    /// never "unexpected". See the class remarks for why this is prefix-matched text rather than a
    /// typed cause.</summary>
    private static readonly string[] DeliberateTriggerPrefixes = ["user unmount", "system suspend"];

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

        foreach (var prefix in DeliberateTriggerPrefixes)
        {
            if (entry.Trigger.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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
