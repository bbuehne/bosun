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
    /// names the level and the top issue; with more than one issue it says how many more.
    /// </remarks>
    public static TrayStatus Compose(AggregateHealth hostHealth, AppHealth appHealth)
    {
        ArgumentNullException.ThrowIfNull(appHealth);

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

    private static AggregateHealth Worse(AggregateHealth a, AggregateHealth b) => a > b ? a : b;
}
