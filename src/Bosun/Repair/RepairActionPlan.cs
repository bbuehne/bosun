using Bosun.Health;

namespace Bosun.Repair;

/// <summary>The one-click actions the health banner and the tray's "Repair" menu offer (bs-aoz, ADR-020
/// Decision 5), plus the banner's "Dismiss".</summary>
public enum RepairActionKind
{
    /// <summary>Stop the current <c>rclone rcd</c> and start a fresh one; every host is then reconciled
    /// from scratch.</summary>
    RestartRclone,

    /// <summary>Drain every mounted host, then re-probe: persistent hosts remount after a fresh probe,
    /// on-demand hosts stay unmounted.</summary>
    UnmountAllAndReprobe,

    /// <summary>Start a replacement Bosun and exit this one. Not counted against the watchdog's limit.</summary>
    RestartBosun,

    /// <summary>Remove the "Bosun restarted itself" notice. Banner only; it repairs nothing.</summary>
    DismissNotice,
}

/// <summary>
/// Which repair buttons the health banner shows, and in what order (bs-aoz). Pure, so the rule is a
/// table in one place that tests can read.
/// </summary>
/// <remarks>
/// <para>
/// <b>All three repairs are always offered</b> while the banner is up. The banner names one cause, but
/// "the top issue is rclone" does not mean Restart Bosun can never help, and a button that disappears
/// when a different issue sorts first is a button the user cannot find when they need it. What the
/// issue changes is the ORDER: the action most likely to fix it leads.
/// </para>
/// <list type="bullet">
/// <item><b>rclone issues</b> (<c>rclone.*</c>): Restart rclone first.</item>
/// <item><b>Everything else</b> (supervisor loop, watchdog, startup, the restart notice, an unknown
/// code): Restart Bosun first. A stalled or dead supervisor cannot be fixed from inside, and a
/// startup problem (config, WinFsp) is fixed by fixing it and starting again.</item>
/// </list>
/// <para>
/// <b>Dismiss</b> is offered, first, only when the top issue is <c>watchdog.restarted</c>: that notice
/// is information, and the thing the user most plausibly wants to do with it is make it go away.
/// </para>
/// </remarks>
public static class RepairActionPlan
{
    /// <summary>The order in the tray's Repair submenu. Fixed: a menu whose items move is hard to use.</summary>
    public static IReadOnlyList<RepairActionKind> TrayOrder { get; } =
        [RepairActionKind.RestartRclone, RepairActionKind.UnmountAllAndReprobe, RepairActionKind.RestartBosun];

    /// <summary>The banner's buttons for an issue with code <paramref name="topIssueCode"/>, in order.</summary>
    public static IReadOnlyList<RepairActionKind> ForIssue(string? topIssueCode)
    {
        if (topIssueCode is null)
        {
            return [];
        }

        if (topIssueCode.StartsWith("rclone.", StringComparison.Ordinal))
        {
            return [RepairActionKind.RestartRclone, RepairActionKind.UnmountAllAndReprobe, RepairActionKind.RestartBosun];
        }

        if (topIssueCode == HealthIssueCodes.WatchdogRestarted)
        {
            return
            [
                RepairActionKind.DismissNotice,
                RepairActionKind.RestartBosun,
                RepairActionKind.RestartRclone,
                RepairActionKind.UnmountAllAndReprobe,
            ];
        }

        return [RepairActionKind.RestartBosun, RepairActionKind.RestartRclone, RepairActionKind.UnmountAllAndReprobe];
    }

    public static string Label(RepairActionKind kind) => kind switch
    {
        RepairActionKind.RestartRclone => "Restart rclone",
        RepairActionKind.UnmountAllAndReprobe => "Unmount all & re-probe",
        RepairActionKind.RestartBosun => "Restart Bosun",
        RepairActionKind.DismissNotice => "Dismiss",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled RepairActionKind value."),
    };
}
