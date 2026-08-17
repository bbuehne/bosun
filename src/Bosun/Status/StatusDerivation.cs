using Bosun.Configuration;
using Bosun.Supervisor;

namespace Bosun.Status;

/// <summary>
/// Pure derivation: <see cref="HostMountSnapshot"/> (+ that host's <see cref="HostConfig"/> and
/// its live session count) in, <see cref="HostStatusRow"/> out; a list of rows in,
/// <see cref="AggregateHealth"/> out. No I/O, no clock, no WPF -- everything here is a plain
/// function of its arguments, which is what makes it exhaustively table-testable (bs-ww9.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as its own static class rather than living inside a polling service.</b>
/// ADR-012 Decision 3's causal-messaging obligation is a DERIVATION problem, not a rendering one:
/// getting the wording right once, and pinning it with tests, beats every consumer (the tray icon,
/// the status window, and any future surface) inventing its own phrasing and drifting apart.
/// </para>
/// <para>
/// <b>Precedence, not independent checks.</b> <see cref="DeriveRow"/> is a ladder of
/// <c>if</c>/<c>else if</c>, evaluated top to bottom, because more than one condition can be true
/// of the same host at once and only one status text can be shown. The ordering itself encodes a
/// judgement call about which cause is most CURRENT/actionable when several are true
/// simultaneously -- see the inline reasoning at each rung.
/// </para>
/// </remarks>
public static class StatusDerivation
{
    public static HostStatusRow DeriveRow(HostMountSnapshot snapshot, HostConfig hostConfig, int sessionCount)
    {
        var drive = hostConfig.Mount.Drive;
        var mode = hostConfig.Mount.Mode;

        var (category, text) = DeriveCategoryAndText(snapshot, hostConfig, drive, mode);

        return new HostStatusRow
        {
            HostKey = snapshot.HostKey,
            DisplayName = hostConfig.DisplayName,
            State = snapshot.State,
            Mode = mode,
            Drive = drive,
            UserParked = snapshot.UserParked,
            SessionCount = sessionCount,
            LastTransitionUtc = snapshot.LastTransitionUtc,
            LastTransitionTrigger = snapshot.LastTransitionTrigger,
            Category = category,
            StatusText = text,
            ConsecutiveMountedFailures = snapshot.ConsecutiveMountedFailures,
            ConsecutiveDeepProbeFailures = snapshot.ConsecutiveDeepProbeFailures,
            ConsecutiveIdleFailures = snapshot.ConsecutiveIdleFailures,
            ConsecutiveMountFailures = snapshot.ConsecutiveMountFailures,
            LastMountFailureReason = snapshot.LastMountFailureReason,
        };
    }

    private static (StatusCategory Category, string Text) DeriveCategoryAndText(
        HostMountSnapshot snapshot, HostConfig hostConfig, string? drive, MountMode mode)
    {
        // Rung 0: mode = "none" is not one of the six causes at all -- there is no drive and never
        // will be. Checked first so nothing below has to special-case a null drive.
        if (mode == MountMode.None)
        {
            return (StatusCategory.NotConfigured, $"{hostConfig.DisplayName}: no mount configured");
        }

        // Rung 1: Mounted is unconditionally healthy. Nothing below can be simultaneously true in
        // a way that should override "it is up and working" -- e.g. ConsecutiveMountFailures is a
        // history of PAST failed attempts to REACH Mounted, not a live condition of a host that is
        // sitting there Mounted right now.
        if (snapshot.State == MountState.Mounted)
        {
            return (StatusCategory.Healthy, $"{drive} is mounted");
        }

        // Rung 2: a user-requested unmount (ADR-015). Checked ahead of every fault case, including
        // process-wide unavailability -- the user does not want this host mounted right now, so
        // "why isn't it mounted" answers with the user's own action, not with WinFsp/reachability
        // detail they did not ask about.
        if (snapshot.UserParked)
        {
            return (StatusCategory.Parked, $"{drive} is parked -- you unmounted it; mount it again from the tray when you're ready");
        }

        // Rung 3: process-wide unavailability (bs-yvw.1). Ahead of this host's own reachability or
        // mount-failure history because it is the more fundamental blocker: even a perfectly
        // reachable, perfectly mountable host cannot enter Mounting while this gate is closed
        // (MountSupervisor.TryBeginMountAsync checks it before the deep probe). ADR-012 Decision 3's
        // own worked example is exactly this rung: "P: is not mounted -- WinFsp is not installed".
        if (snapshot.MountUnavailableReason is { Length: > 0 } reason)
        {
            return (StatusCategory.MountingUnavailable, $"{drive} is not mounted -- {reason}");
        }

        // Rung 4: genuinely unreachable right now. Ahead of the mount-failure ladder (rung 5) on
        // purpose -- a host can carry a nonzero ConsecutiveMountFailures from an EARLIER cycle (that
        // counter only resets on a successful mount) while ALSO being Unreachable at this exact
        // moment for an unrelated, more current reason. "It cannot even be reached" is the fresher
        // and more actionable fact; reporting a stale mount-failure count instead would be exactly
        // the misdirection ADR-012 Decision 3 exists to prevent.
        if (snapshot.State == MountState.Unreachable)
        {
            var attempts = snapshot.ConsecutiveIdleFailures;
            var text = attempts > 0
                ? $"{drive} is not mounted -- host unreachable after {attempts} consecutive failed {(attempts == 1 ? "probe" : "probes")}"
                : $"{drive} is not mounted -- host unreachable";
            return (StatusCategory.Unreachable, text);
        }

        // Rung 5: bs-ww9.4 -- reachable, but repeated mount attempts have failed. This is the case
        // that was previously invisible: TCP answers fine (never reaches Unreachable above), so
        // without this rung the host would fall all the way to the Pending catch-all and look like
        // nothing more than an ordinary in-progress retry, forever.
        if (snapshot.ConsecutiveMountFailures > 0)
        {
            var count = snapshot.ConsecutiveMountFailures;
            var times = count == 1 ? "1 time" : $"{count} times";
            var lastError = string.IsNullOrEmpty(snapshot.LastMountFailureReason) ? "unknown error" : snapshot.LastMountFailureReason;
            return (StatusCategory.MountFailing, $"{drive} is not mounted -- mount failed {times}; last error: {lastError}");
        }

        // Rung 6: on-demand resting in Ready (docs/ARCHITECTURE.md §4 rule 6) -- not a fault, it is
        // exactly where an idle on-demand host is supposed to sit.
        if (snapshot.State == MountState.Ready && mode == MountMode.OnDemand)
        {
            return (StatusCategory.OnDemandIdle, $"{drive} is not mounted -- on-demand; mount it from the tray when you need it");
        }

        // Rung 7: everything else is transitional/administrative, expected to resolve on its own
        // within one probe/mount cycle -- not one of the six causes, and not a fault.
        var pendingText = snapshot.State switch
        {
            MountState.Probing => $"{drive}: checking whether {hostConfig.DisplayName} is reachable...",
            MountState.Ready => $"{drive}: ready to mount",
            MountState.Mounting => $"{drive}: mounting...",
            MountState.Draining => $"{drive}: unmounting...",
            MountState.Disabled => $"{drive}: waiting to start",
            _ => $"{drive}: {snapshot.State}",
        };
        return (StatusCategory.Pending, pendingText);
    }

    /// <summary>
    /// Aggregate health across every row, for the tray icon (ADR-012 Decision 3, bs-ww9.6). Derived
    /// from the exact same rows the status window renders so the two surfaces can never disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Process-wide unavailability is always <see cref="AggregateHealth.Error"/>.</b> Nothing a
    /// host-level condition can do outranks "nothing can mount right now, for any host".
    /// </para>
    /// <para>
    /// <b>A persistent host repeatedly failing to mount is <see cref="AggregateHealth.Error"/>,
    /// never merely <see cref="AggregateHealth.Degraded"/>.</b> bs-ww9.4 item 3's reasoning: "the
    /// user asked for that drive at every login and is not getting it", which will not resolve
    /// itself. An ON-DEMAND host failing the same way does NOT escalate the icon at all (stays
    /// whatever it would otherwise be) -- per the same item, a manual mount click that fails already
    /// gives the user immediate feedback from the click itself, so the icon does not also need to
    /// carry it.
    /// </para>
    /// <para>
    /// <b>A persistent host that is merely <see cref="StatusCategory.Unreachable"/>, or a
    /// <see cref="MountState.Mounted"/> host accumulating (but not yet over threshold) probe
    /// failures, is <see cref="AggregateHealth.Degraded"/>.</b> Both are ADR-005's ordinary
    /// "drives disappear and reappear" story -- worth a glance, but the supervisor's own retry/
    /// unmount-on-failure machinery is already handling it without the user needing to act.
    /// </para>
    /// <para>
    /// <b><see cref="StatusCategory.Parked"/> and <see cref="StatusCategory.OnDemandIdle"/> never
    /// contribute anything.</b> ADR-015 and rule 6 are explicit that neither is a fault; an icon
    /// that turned amber because the user parked a drive on purpose would be exactly the
    /// "why does this look broken" confusion ADR-015 set out to avoid.
    /// </para>
    /// </remarks>
    public static AggregateHealth DeriveAggregateHealth(IReadOnlyList<HostStatusRow> rows)
    {
        var sawDegraded = false;

        foreach (var row in rows)
        {
            if (row.Category == StatusCategory.MountingUnavailable)
            {
                return AggregateHealth.Error;
            }

            if (row.Category == StatusCategory.MountFailing && row.Mode == MountMode.Persistent)
            {
                return AggregateHealth.Error;
            }

            if (row.Category == StatusCategory.Unreachable && row.Mode == MountMode.Persistent)
            {
                sawDegraded = true;
            }

            if (row.State == MountState.Mounted && (row.ConsecutiveMountedFailures > 0 || row.ConsecutiveDeepProbeFailures > 0))
            {
                sawDegraded = true;
            }
        }

        return sawDegraded ? AggregateHealth.Degraded : AggregateHealth.Healthy;
    }
}
