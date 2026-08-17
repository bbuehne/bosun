using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bosun.UI.Tray;

/// <summary>
/// Renders a <see cref="TrayIconAppearance"/> (plain data, unit-tested via
/// <see cref="TrayIconAppearanceSelector"/>) into an <see cref="ImageSource"/> the tray can show:
/// Bosun's anchor mark in the health colour, with a corner badge when something is wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a badge and not colour alone.</b> ADR-012 Decision 3 requires a degraded Bosun to be
/// distinguishable at a glance. Colour alone fails that for a colourblind user, and hue
/// differences are easy to miss at 16px anyway, so the two unhealthy states also carry a shape:
/// a filled disc in the corner with a glyph. Healthy carries no badge — "nothing is wrong" is best
/// said by the absence of a mark, and it keeps the common case visually quiet.
/// </para>
/// <para>
/// Pure drawing code; the decision of WHICH appearance to use lives in
/// <see cref="TrayIconAppearanceSelector"/> and is tested there.
/// </para>
/// </remarks>
internal static class GeneratedIconSourceFactory
{
    /// <summary>Rendered at 32px: the largest size the notification area normally asks for, and a
    /// clean 2x of the 16px baseline so downscaling stays sharp.</summary>
    public const int IconSize = 32;

    /// <summary>How much of the canvas the anchor occupies when a badge is also drawn. Tuned by
    /// rendering all three states at 16px and 32px and looking at them: at full extent the badge
    /// covers the crown and the mark stops reading as an anchor.</summary>
    private const double MarkExtentWhenBadged = 0.80;

    /// <summary>Badge radius as a fraction of the icon. Large enough that its PRESENCE is visible
    /// at 16px (where the glyph inside it is not legible, and is not expected to be -- the shape
    /// difference is what carries at that size), small enough to leave the anchor recognisable.</summary>
    private const double BadgeRadius = 0.24;

    public static ImageSource Create(TrayIconAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var markBrush = ToBrush(appearance.ColorHex);
        var mark = AnchorMarkGeometry.Create(markBrush);

        var badged = !string.IsNullOrEmpty(appearance.Glyph);

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            // When a badge is present the mark is inset toward the top-left so the badge has a
            // corner to sit in. Without this the badge lands on top of the crown and the anchor
            // stops being an anchor -- which is the opposite of what a status overlay should do.
            var extent = badged ? IconSize * MarkExtentWhenBadged : IconSize;
            var scale = extent / AnchorMarkGeometry.NominalSize;

            ctx.PushTransform(new ScaleTransform(scale, scale));
            ctx.DrawDrawing(mark);
            ctx.Pop();

            if (badged)
            {
                DrawBadge(ctx, appearance);
            }
        }

        var bitmap = new RenderTargetBitmap(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawBadge(DrawingContext ctx, TrayIconAppearance appearance)
    {
        var radius = IconSize * BadgeRadius;
        var centre = new Point(IconSize - radius - 1, IconSize - radius - 1);
        var badgeBrush = ToBrush(appearance.ColorHex);

        // White outline, badge filled in the same health colour as the mark. The ring is what
        // separates the badge from the fluke it overlaps -- without it the two merge into one
        // shape at 16px and the glyph stops reading, which defeats the point of having a glyph.
        var outline = new Pen(Brushes.White, IconSize * 0.08);
        outline.Freeze();
        ctx.DrawEllipse(badgeBrush, outline, centre, radius, radius);

        var text = new FormattedText(
            appearance.Glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            radius * 1.9,
            Brushes.White,
            1.0);

        ctx.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
    }

    private static Brush ToBrush(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
