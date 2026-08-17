using System.Windows;
using System.Windows.Media;

namespace Bosun.UI.Tray;

/// <summary>
/// Bosun's mark, drawn as vector geometry: a single admiralty-pattern anchor (ring, stock, shank,
/// crown, flukes) in a nominal 100x100 box, scaled at render time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a SINGLE anchor here and not the crossed pair.</b> Bosun's identity mark is the
/// boatswain's crossed fouled anchors, and that is what the application icon uses. It is
/// deliberately NOT what the tray uses. Rendered at 16px — the size Windows actually asks for in
/// the notification area — the negative space between two crossed anchors falls below one pixel
/// and the whole thing collapses into an indistinct blob. This was measured, not assumed: the
/// crossed mark and this one were both rendered at 16, 24 and 32px and compared. At 16px the
/// crossed version is unreadable; the single anchor still resolves into ring, stock, shank and
/// crown.
/// </para>
/// <para>
/// Simplifying a mark as it gets smaller is ordinary icon practice — a Windows .ico carries
/// several sizes precisely so each can be drawn appropriately — so this is not a compromise of the
/// identity, it is the identity rendered legibly. If someone later replaces this with the crossed
/// pair "for consistency", the tray icon becomes a smudge and ADR-012 Decision 3's whole premise
/// (a degraded Bosun must be distinguishable AT A GLANCE) quietly stops holding.
/// </para>
/// <para>
/// Vector rather than a shipped bitmap so it stays crisp at any DPI, can be recoloured per health
/// state without shipping one asset per state, and adds nothing to the single-file publish.
/// </para>
/// </remarks>
internal static class AnchorMarkGeometry
{
    /// <summary>The coordinate space the geometry below is authored in.</summary>
    public const double NominalSize = 100d;

    private static readonly RectangleGeometry Shank =
        new(new Rect(45.5, 20, 9, 58));

    private static readonly RectangleGeometry Stock =
        new(new Rect(27, 29, 46, 8));

    private static readonly EllipseGeometry Ring =
        new(new Point(50, 13), 8, 8);

    // A U opening upward. Written as explicit cubic Beziers rather than an SVG elliptical arc:
    // arc sweep/large-arc flags are easy to get backwards (and were, twice, during authoring),
    // whereas the control points here say plainly where the curve goes.
    private static readonly Geometry Crown =
        Geometry.Parse("M 18,50 C 18,74 32,86 50,86 C 68,86 82,74 82,50");

    private static readonly Geometry LeftFluke = Geometry.Parse("M 6,36 L 27,47 L 16,58 Z");
    private static readonly Geometry RightFluke = Geometry.Parse("M 94,36 L 73,47 L 84,58 Z");

    /// <summary>Builds the anchor as a frozen <see cref="Drawing"/> in the given brush.</summary>
    public static Drawing Create(Brush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        var group = new DrawingGroup();

        var ringPen = new Pen(brush, 7);
        ringPen.Freeze();
        group.Children.Add(new GeometryDrawing { Geometry = Ring, Pen = ringPen });

        group.Children.Add(new GeometryDrawing { Geometry = Shank, Brush = brush });
        group.Children.Add(new GeometryDrawing { Geometry = Stock, Brush = brush });

        var crownPen = new Pen(brush, 9)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        crownPen.Freeze();
        group.Children.Add(new GeometryDrawing { Geometry = Crown, Pen = crownPen });

        group.Children.Add(new GeometryDrawing { Geometry = LeftFluke, Brush = brush });
        group.Children.Add(new GeometryDrawing { Geometry = RightFluke, Brush = brush });

        group.Freeze();
        return group;
    }
}
