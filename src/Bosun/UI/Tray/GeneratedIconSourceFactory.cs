using System.Windows.Media;
using H.NotifyIcon;

namespace Bosun.UI.Tray;

/// <summary>
/// Turns a <see cref="TrayIconAppearance"/> (plain data, unit-tested via
/// <see cref="TrayIconAppearanceSelector"/>) into an actual <see cref="ImageSource"/> H.NotifyIcon
/// can show. Pure drawing code -- no logic worth testing lives here (CLAUDE.md/bs-ww9.5 brief:
/// "the drawing itself does not need tests").
/// </summary>
/// <remarks>
/// Uses H.NotifyIcon's own <see cref="GeneratedIconSource"/> (a <see cref="BitmapSource"/>
/// subclass, ships in the H.GeneratedIcons.System.Drawing dependency pulled in by
/// H.NotifyIcon.Wpf) rather than hand-rolling icon generation -- it renders a solid rounded
/// background plus short text entirely in managed WPF drawing code, with no need for
/// <c>System.Drawing.Common</c> or a shipped <c>.ico</c> asset.
/// </remarks>
internal static class GeneratedIconSourceFactory
{
    public const int IconSize = 32;

    public static ImageSource Create(TrayIconAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var background = (Brush)new BrushConverter().ConvertFromString(appearance.ColorHex)!;
        background.Freeze();

        return new GeneratedIconSource
        {
            Background = background,
            Foreground = Brushes.White,
            Text = appearance.Glyph,
            Size = IconSize,
        };
    }
}
