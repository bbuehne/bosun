namespace Bosun.UI;

/// <summary>
/// Pure geometry: produces a sane default placement, and clamps a persisted one to the CURRENT
/// virtual screen (bs-ww9.3 / ADR-018 rule 1). This is the guard against the failure mode named
/// explicitly in the brief: "restoring a window to coordinates on a monitor that is no longer
/// attached puts it off-screen where the user cannot reach it." A laptop docked at one monitor
/// layout yesterday and undocked today, or a monitor simply unplugged, must never produce a
/// window the user cannot see or drag back.
/// </summary>
public static class WindowPlacementClamper
{
    public const double MinWidth = 400;
    public const double MinHeight = 300;
    public const double DefaultWidth = 900;
    public const double DefaultHeight = 600;

    /// <summary>First run, or a corrupt/missing persisted placement: a sensible size, centered on
    /// the current virtual screen.</summary>
    public static WindowPlacement CreateDefault(ScreenBounds virtualScreen)
    {
        var width = SanitizeDimension(DefaultWidth, DefaultWidth, MinWidth, virtualScreen.Width);
        var height = SanitizeDimension(DefaultHeight, DefaultHeight, MinHeight, virtualScreen.Height);

        return new WindowPlacement
        {
            Left = virtualScreen.Left + (virtualScreen.Width - width) / 2,
            Top = virtualScreen.Top + (virtualScreen.Height - height) / 2,
            Width = width,
            Height = height,
            IsMaximized = false,
        };
    }

    /// <summary>
    /// Clamps a persisted placement so it is fully reachable on <paramref name="virtualScreen"/>:
    /// width/height are capped to fit (never below <see cref="MinWidth"/>/<see cref="MinHeight"/>),
    /// and left/top are pulled back inside the screen bounds. A non-finite or non-positive
    /// dimension (a corrupt persisted file) falls back to the default size rather than propagating
    /// NaN/negative geometry into a real <see cref="System.Windows.Window"/>.
    /// </summary>
    public static WindowPlacement Clamp(WindowPlacement placement, ScreenBounds virtualScreen)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var width = SanitizeDimension(placement.Width, DefaultWidth, MinWidth, virtualScreen.Width);
        var height = SanitizeDimension(placement.Height, DefaultHeight, MinHeight, virtualScreen.Height);

        // The window must fit entirely within the virtual screen if at all possible: the maximum
        // allowed Left/Top is "right/bottom edge of the screen minus the window's own size". When
        // the window is wider/taller than the screen (a huge saved size, or a much smaller screen
        // than before), Math.Max below pins it to the screen's own left/top edge instead of letting
        // the max go negative -- clamping to a reachable, if imperfect, position rather than
        // solving an impossible fit.
        var maxLeft = Math.Max(virtualScreen.Left, virtualScreen.Right - width);
        var maxTop = Math.Max(virtualScreen.Top, virtualScreen.Bottom - height);

        var left = double.IsFinite(placement.Left)
            ? Math.Clamp(placement.Left, virtualScreen.Left, maxLeft)
            : virtualScreen.Left;
        var top = double.IsFinite(placement.Top)
            ? Math.Clamp(placement.Top, virtualScreen.Top, maxTop)
            : virtualScreen.Top;

        return placement with { Left = left, Top = top, Width = width, Height = height };
    }

    private static double SanitizeDimension(double value, double fallback, double min, double screenExtent)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            value = fallback;
        }

        // Never let the screen being smaller than `min` produce an invalid Clamp range (min must
        // be <= max) -- a tiny or misreported virtual screen still gets a window at least `min`
        // in size, even if that means it does not fully fit. Better than a degenerate 0x0 window.
        var upperBound = Math.Max(min, screenExtent);
        return Math.Clamp(value, min, upperBound);
    }
}
