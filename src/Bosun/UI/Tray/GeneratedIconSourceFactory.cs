using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bosun.UI.Tray;

/// <summary>
/// Renders a <see cref="TrayIconAppearance"/> (plain data, unit-tested via
/// <see cref="TrayIconAppearanceSelector"/>) into a <see cref="System.Drawing.Icon"/> for
/// <c>TaskbarIcon.Icon</c>: Bosun's anchor mark in the health colour, with a corner badge when
/// something is wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a <see cref="System.Drawing.Icon"/> and not <c>TaskbarIcon.IconSource</c>.</b> The
/// <c>IconSource</c> path runs through H.NotifyIcon's <c>ImageExtensions.ToStream(ImageSource)</c>,
/// which switches on the concrete type and handles exactly two: <c>BitmapImage</c> (read via its
/// <c>UriSource</c>) and <c>BitmapFrame</c> (converted to a URI string). <b>Both resolve a URI.</b>
/// An icon generated at runtime has no URI — there is no file and no pack resource behind it — so
/// that path cannot work for us at all, whatever ImageSource type we hand it.
/// </para>
/// <para>
/// This is not theoretical. Handing it a <see cref="RenderTargetBitmap"/> threw
/// <c>NotImplementedException: ImageSource type: ... RenderTargetBitmap is not supported</c> from
/// H.NotifyIcon's own dispatcher continuation, which is an <b>unhandled</b> exception on the
/// dispatcher and killed the whole process — taking the supervisor with it and orphaning a live
/// mount. <c>TaskbarIcon.Icon</c> takes a <see cref="System.Drawing.Icon"/> directly and skips the
/// conversion entirely.
/// </para>
/// <para>
/// The icon is built by rendering to a <see cref="RenderTargetBitmap"/>, encoding that as PNG, and
/// wrapping it in a minimal single-image ICO container. PNG-compressed ICO entries are supported
/// from Windows Vista onward. Going via a container rather than
/// <c>Icon.FromHandle(bitmap.GetHicon())</c> deliberately avoids owning an unmanaged HICON that
/// would have to be destroyed by hand on every health change.
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
    /// at 16px (where the glyph inside it is not legible, and is not expected to be — the shape
    /// difference is what carries at that size), small enough to leave the anchor recognisable.</summary>
    private const double BadgeRadius = 0.24;

    public static System.Drawing.Icon Create(TrayIconAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var png = RenderPng(appearance);
        using var ico = new MemoryStream();
        WriteSingleImageIcoContainer(ico, png, IconSize);
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    private static byte[] RenderPng(TrayIconAppearance appearance)
    {
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

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Wraps a single PNG in an ICO container: a 6-byte ICONDIR, one 16-byte ICONDIRENTRY,
    /// then the PNG bytes. See scripts/build-icon.ps1, which writes the same structure for the
    /// multi-size application icon.</summary>
    private static void WriteSingleImageIcoContainer(Stream target, byte[] png, int size)
    {
        using var writer = new BinaryWriter(target, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);    // reserved
        writer.Write((ushort)1);    // type: icon
        writer.Write((ushort)1);    // image count

        writer.Write((byte)(size >= 256 ? 0 : size));   // width  (0 means 256)
        writer.Write((byte)(size >= 256 ? 0 : size));   // height
        writer.Write((byte)0);      // palette entries
        writer.Write((byte)0);      // reserved
        writer.Write((ushort)1);    // colour planes
        writer.Write((ushort)32);   // bits per pixel
        writer.Write((uint)png.Length);
        writer.Write((uint)(6 + 16));  // offset: past the directory

        writer.Write(png);
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
