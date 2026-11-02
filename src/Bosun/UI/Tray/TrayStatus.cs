using Bosun.Health;
using Bosun.Status;

namespace Bosun.UI.Tray;

/// <summary>What the tray shows right now: which of the existing three icons, and the tooltip text.</summary>
/// <param name="IconHealth">Fed to <see cref="TrayIconAppearanceSelector"/>.</param>
/// <param name="Tooltip">At most <see cref="TrayStatus.MaxTooltipLength"/> characters.</param>
public sealed record TrayStatus(AggregateHealth IconHealth, string Tooltip)
{
    /// <summary>The Win32 notify-icon tooltip holds 127 characters plus the terminator; anything
    /// longer is cut by the shell without warning.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Combines the host roll-up with the app's own health (bs-yyg, ADR-020 Decision 4). The tray
    /// icon is the only always-on surface (ADR-018 rule 5), so an app-level fault must reach it even
    /// when every host row looks fine -- the supervisor loop dying leaves rows frozen at whatever
    /// they last said.
    /// </summary>
    /// <remarks>
    /// The icon is the worse of the two levels (Faulted maps to the existing red, Degraded to the
    /// existing amber -- no new artwork). While the app is still starting, the host roll-up is held
    /// at Degraded at most: every mountable host reads "unavailable" until rclone is up, and a red
    /// icon for the first seconds of every launch would teach the user to ignore red. The tooltip
    /// names the level and the top issue; with more than one issue it says how many more. When the
    /// app is fine and a host is the problem, the tooltip names that host's cause instead (bs-ww9.1.2).
    /// </remarks>
    public static TrayStatus Compose(AggregateHealth hostHealth, AppHealth appHealth, IReadOnlyList<HostStatusRow> rows)
    {
        ArgumentNullException.ThrowIfNull(appHealth);
        ArgumentNullException.ThrowIfNull(rows);

        var hosts = appHealth.IsStarting && hostHealth == AggregateHealth.Error ? AggregateHealth.Degraded : hostHealth;

        var iconHealth = appHealth.Level switch
        {
            HealthLevel.Faulted => AggregateHealth.Error,
            HealthLevel.Degraded => Worse(hosts, AggregateHealth.Degraded),
            _ => hosts,
        };

        string tooltip;
        if (appHealth.TopIssue is { } top)
        {
            var more = appHealth.Issues.Count - 1;
            tooltip = $"Bosun — {appHealth.Level}: {top.Title}" + (more > 0 ? $" (+{more} more)" : string.Empty);
        }
        else if (appHealth.IsStarting)
        {
            tooltip = "Bosun — starting…";
        }
        else if (hostHealth != AggregateHealth.Healthy && HostCause(rows) is { } hostCause)
        {
            // bs-ww9.1.2: the app is fine, so the problem is on a host. Name it; the old generic
            // "mounting unavailable" was wrong for, say, a persistent host failing to mount.
            tooltip = hostCause;
        }
        else
        {
            tooltip = TrayIconAppearanceSelector.Select(iconHealth).AccessibleName;
        }

        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..(MaxTooltipLength - 1)] + "…";
        }

        return new TrayStatus(iconHealth, tooltip);
    }

    /// <summary>
    /// "Bosun — P: is not mounted -- mount failed 3 times": the status text of the host row that
    /// contributes most to the roll-up (the first, when several tie), with "(+N more)" when other
    /// rows contribute too. The row is chosen with <see cref="StatusDerivation.ContributionOf"/>, the
    /// question the roll-up itself asked, so the tooltip cannot blame a different host than the icon.
    /// Null when no row contributes.
    /// </summary>
    /// <remarks>
    /// Over the Win32 limit, the trailing "; ..." clauses of the cause are dropped first (a mount
    /// failure's "; last error: ..." is the long part, and the count before it is the cause), and
    /// only then is the text cut with an ellipsis. The "(+N more)" suffix is always kept.
    /// </remarks>
    private static string? HostCause(IReadOnlyList<HostStatusRow> rows)
    {
        HostStatusRow? worst = null;
        var worstLevel = AggregateHealth.Healthy;
        var contributing = 0;

        foreach (var row in rows)
        {
            var level = StatusDerivation.ContributionOf(row);
            if (level == AggregateHealth.Healthy)
            {
                continue;
            }

            contributing++;
            if (worst is null || level > worstLevel)
            {
                worst = row;
                worstLevel = level;
            }
        }

        if (worst is null)
        {
            return null;
        }

        const string prefix = "Bosun — ";
        var suffix = contributing > 1 ? $" (+{contributing - 1} more)" : string.Empty;
        var budget = MaxTooltipLength - prefix.Length - suffix.Length;

        var cause = worst.StatusText;
        while (cause.Length > budget)
        {
            var clause = cause.LastIndexOf("; ", StringComparison.Ordinal);
            if (clause < 0)
            {
                break;
            }

            cause = cause[..clause];
        }

        if (cause.Length > budget)
        {
            cause = cause[..(budget - 1)].TrimEnd() + "…";
        }

        return prefix + cause + suffix;
    }

    private static AggregateHealth Worse(AggregateHealth a, AggregateHealth b) => a > b ? a : b;
}
