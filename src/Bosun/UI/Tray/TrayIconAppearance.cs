using Bosun.Status;
namespace Bosun.UI.Tray;

/// <summary>
/// A drawing-agnostic description of what the tray icon should look like for a given
/// <see cref="AggregateHealth"/>. Deliberately not a WPF <c>Brush</c>/<c>ImageSource</c> -- keeping
/// this a plain data record means <see cref="TrayIconAppearanceSelector.Select"/> is unit-testable
/// without touching WPF media types, matching the bs-ww9.5 brief's explicit testability
/// requirement ("mapping aggregate health -&gt; which icon to display"). The actual rendering into
/// an <c>ImageSource</c> H.NotifyIcon can use is untested drawing code -- see
/// <c>GeneratedIconSourceFactory</c>.
/// </summary>
public sealed record TrayIconAppearance
{
    /// <summary>#RRGGBB. Chosen from a small, deliberately high-contrast palette -- not decorative.</summary>
    public required string ColorHex { get; init; }

    /// <summary>
    /// A short glyph drawn over the color, so the three states remain distinguishable by shape
    /// alone, not color alone (colorblind-accessible; also legible at the tiny sizes a tray icon
    /// is rendered at where a subtle hue difference is easy to miss). Empty string for the healthy
    /// state -- "nothing is wrong" is well represented by the absence of a mark.
    /// </summary>
    public required string Glyph { get; init; }

    /// <summary>Human-readable name for the icon's accessible name / tooltip prefix. NOT a
    /// substitute for the causal per-host text bs-ww9.6 produces -- see ADR-012 Decision 3's
    /// "not a tooltip; nobody hovers".</summary>
    public required string AccessibleName { get; init; }
}

/// <summary>
/// Pure mapping from <see cref="AggregateHealth"/> to <see cref="TrayIconAppearance"/> (bs-ww9.5:
/// "mapping aggregate health -&gt; which icon to display", explicitly called out as needing its
/// own tests). Exhaustive by construction -- an unmapped <see cref="AggregateHealth"/> value
/// throws rather than silently falling through to a default appearance, so adding a new health
/// state without updating this selector is a loud failure, not a healthy-looking icon lying about
/// a new degraded state.
/// </summary>
public static class TrayIconAppearanceSelector
{
    public static TrayIconAppearance Select(AggregateHealth health) => health switch
    {
        AggregateHealth.Healthy => new TrayIconAppearance
        {
            ColorHex = "#2E7D32",
            Glyph = "",
            AccessibleName = "Bosun — healthy",
        },
        AggregateHealth.Degraded => new TrayIconAppearance
        {
            ColorHex = "#F9A825",
            Glyph = "!",
            AccessibleName = "Bosun — degraded",
        },
        AggregateHealth.Error => new TrayIconAppearance
        {
            ColorHex = "#C62828",
            Glyph = "×",
            AccessibleName = "Bosun — mounting unavailable",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(health), health, "Unhandled AggregateHealth value."),
    };
}
