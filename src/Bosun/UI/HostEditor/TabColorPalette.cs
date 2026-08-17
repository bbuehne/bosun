using System.Windows.Media;

namespace Bosun.UI.HostEditor;

/// <summary>
/// The color choices offered for a host's Terminal tab, as <c>#RRGGBB</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A curated palette rather than the system color dialog, and deliberately so.</b> The obvious
/// implementation was Windows' own <c>ColorDialog</c>, but WPF ships no color dialog, so that
/// needs <c>UseWindowsForms</c> — which pulls Windows Forms types into scope across the whole
/// project and immediately collided with WPF's own <c>Application</c>, <c>Brush</c> and
/// <c>Control</c> in unrelated files. Enabling a second UI framework, and disambiguating code that
/// has nothing to do with color, is a poor trade for one dialog.
/// </para>
/// <para>
/// It is also arguably the wrong tool. A tab color exists to tell one host's terminal from
/// another's at a glance, so what is wanted is a set of colors that are mutually distinguishable
/// and legible against a dark terminal — not sixteen million shades, most of which fail at least
/// one of those. The palette below is chosen for separation in hue and for contrast on the dark
/// backgrounds Terminal's built-in schemes use.
/// </para>
/// <para>
/// The hex field remains editable next to the swatches, so an exact color can still be typed or
/// pasted. Nothing here is a restriction on what can be saved.
/// </para>
/// </remarks>
public static class TabColorPalette
{
    public static IReadOnlyList<TabColorChoice> Choices { get; } =
    [
        new("#2D5F3F", "Forest"),
        new("#1F6F8B", "Teal"),
        new("#2E4A8B", "Indigo"),
        new("#5B3E8E", "Violet"),
        new("#8B2E5F", "Plum"),
        new("#A83232", "Crimson"),
        new("#B35C00", "Amber"),
        new("#7A6A1F", "Olive"),
        new("#00695C", "Pine"),
        new("#37474F", "Slate"),
        new("#4E342E", "Cocoa"),
        new("#546E7A", "Steel"),
    ];
}

/// <summary>One palette entry: the value written to config, and a name for the tooltip.</summary>
public sealed record TabColorChoice(string Hex, string Name)
{
    /// <summary>A frozen brush for the swatch, so the view needs no converter.</summary>
    public Brush Swatch { get; } = CreateFrozenBrush(Hex);

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
