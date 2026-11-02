using Bosun.Status;
using Bosun.Supervisor;

namespace Bosun.UI;

/// <summary>How a host row should look in the window (bs-ww9.1.2). The window maps each value to a
/// colour in XAML; which tone a row gets is decided here, where it is tested.</summary>
public enum HostRowTone
{
    /// <summary>Nothing to say: transitional, on-demand idle, or no mount configured.</summary>
    Neutral,

    /// <summary>Mounted and working.</summary>
    Healthy,

    /// <summary>Deliberately not mounted -- parked at the user's own request (ADR-015). Quiet, never
    /// fault-coloured.</summary>
    Muted,

    /// <summary>Something to glance at that may resolve itself: unreachable, or waiting on an
    /// app-wide cause the health banner already names.</summary>
    Warning,

    /// <summary>A host that will not come right without the user: mounting keeps failing, or its
    /// rclone remote could not be set up.</summary>
    Error,
}

/// <summary>
/// What the window and the tray show for one host row (bs-ww9.1.2, ADR-012 Decision 3, ADR-015).
/// Derived from <see cref="HostStatusRow.Category"/>, not from the raw <see cref="MountState"/>:
/// the state alone cannot tell a parked host from a broken one (both idle in Ready or Unreachable),
/// which is how the State column came to say "Unreachable" beside "P: is parked". Pure data in, pure
/// data out, so the whole mapping is table-tested; the WPF code only binds to it.
/// </summary>
/// <remarks>
/// Also the item type of the window's grid, so the grid binds its columns to this record's
/// properties and the row's own <see cref="Row"/> is what the context menu acts on.
/// </remarks>
public sealed record HostRowPresentation
{
    /// <summary>The row this was derived from. The context menu and its actions use it.</summary>
    public required HostStatusRow Row { get; init; }

    public string DisplayName => Row.DisplayName;

    public string? Drive => Row.Drive;

    public string StatusText => Row.StatusText;

    /// <summary>The State column's text, e.g. "Parked". Never the raw <see cref="MountState"/> name.</summary>
    public required string StateLabel { get; init; }

    /// <summary>A one-character mark, so the states differ by shape and not by colour alone.</summary>
    public required string Glyph { get; init; }

    public required HostRowTone Tone { get; init; }

    /// <summary>The State column's full cell text: the glyph, then <see cref="StateLabel"/>.</summary>
    public string StateDisplay => $"{Glyph} {StateLabel}";

    /// <summary>The tray menu item's header, e.g. "Mercury — parked" or "Mercury — mounted (P:)".</summary>
    public required string TrayHeader { get; init; }

    public static HostRowPresentation Create(HostStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var mounted = row.State == MountState.Mounted;

        var (label, glyph, tone) = row.Category switch
        {
            StatusCategory.Healthy => ("Mounted", "●", HostRowTone.Healthy),
            StatusCategory.OnDemandIdle => ("On demand", "○", HostRowTone.Neutral),
            StatusCategory.Parked => ("Parked", "‖", HostRowTone.Muted),
            StatusCategory.MountingUnavailable => ("Mounting unavailable", "▲", HostRowTone.Warning),
            StatusCategory.MountFailing => ("Mount failing", "×", HostRowTone.Error),
            StatusCategory.Unreachable => ("Unreachable", "▲", HostRowTone.Warning),

            // The remote could not be written. A host that is nevertheless mounted may be serving
            // the wrong machine (bs-0tn), so it keeps the Mounted word and is flagged; one that is
            // not mounted cannot mount at all.
            StatusCategory.ProvisioningFailed => mounted
                ? ("Mounted, setup failed", "▲", HostRowTone.Warning)
                : ("Setup failed", "×", HostRowTone.Error),

            // bs-obf: mounted, but rclone reports files it could not upload; the idle unmount is held
            // back until they clear, so the drive stays up and is flagged rather than shown as healthy.
            StatusCategory.UploadsFailing => ("Mounted, uploads failing", "▲", HostRowTone.Warning),

            StatusCategory.NotConfigured => ("No mount", "–", HostRowTone.Neutral),
            StatusCategory.Pending => (PendingLabel(row.State), "…", HostRowTone.Neutral),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row.Category, "Unhandled StatusCategory value."),
        };

        return new HostRowPresentation
        {
            Row = row,
            StateLabel = label,
            Glyph = glyph,
            Tone = tone,
            TrayHeader = $"{row.DisplayName} — {ShortStatus(row, label)}",
        };
    }

    // The same words StatusDerivation's pending text uses ("checking", "mounting...", "unmounting...").
    private static string PendingLabel(MountState state) => state switch
    {
        MountState.Probing => "Checking",
        MountState.Ready => "Ready",
        MountState.Mounting => "Mounting",
        MountState.Draining => "Unmounting",
        MountState.Disabled => "Starting",
        _ => state.ToString(),
    };

    // The tray header is the label in lower case, plus the drive letter for a mounted host -- the
    // one fact a tray user wants without opening anything.
    private static string ShortStatus(HostStatusRow row, string label)
    {
        var text = label.ToLowerInvariant();
        return row.Category == StatusCategory.Healthy && row.Drive is { Length: > 0 } drive
            ? $"{text} ({drive})"
            : text;
    }
}
