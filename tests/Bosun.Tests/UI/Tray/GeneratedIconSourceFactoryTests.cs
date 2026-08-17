using Bosun.Status;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// Actually constructs the tray icon for every health state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Bosun shipped a tray icon that killed the process on launch. The
/// factory returned a <c>RenderTargetBitmap</c> and the code assigned it to
/// <c>TaskbarIcon.IconSource</c>; H.NotifyIcon's <c>ImageExtensions.ToStream(ImageSource)</c>
/// switches on the concrete type, handles only <c>BitmapImage</c> and <c>BitmapFrame</c>, and
/// threw <c>NotImplementedException</c> for anything else — on its own dispatcher continuation,
/// where nothing in Bosun could catch it. The process died with a mount up and orphaned it.
/// </para>
/// <para>
/// <b>Why the suite did not catch it.</b> 729 tests passed. Every one of them exercised the
/// DECISION of which appearance to use (<see cref="TrayIconAppearanceSelector"/>) and none of them
/// exercised BUILDING the icon from that appearance, because the drawing was written off as
/// "untestable WPF glue". The distinction was wrong: choosing an unsupported type is a logic bug
/// that a construction call catches immediately.
/// </para>
/// <para>
/// Nothing here needs a window, a message pump, or a real tray. It constructs an icon in memory
/// and throws it away, so it is safe in the default suite.
/// </para>
/// </remarks>
public sealed class GeneratedIconSourceFactoryTests
{
    [Theory]
    [InlineData(AggregateHealth.Healthy)]
    [InlineData(AggregateHealth.Degraded)]
    [InlineData(AggregateHealth.Error)]
    public void Create_ProducesAUsableIcon_ForEveryHealthState(AggregateHealth health)
    {
        var appearance = TrayIconAppearanceSelector.Select(health);

        using var icon = GeneratedIconSourceFactory.Create(appearance);

        Assert.NotNull(icon);
        Assert.Equal(GeneratedIconSourceFactory.IconSize, icon.Width);
        Assert.Equal(GeneratedIconSourceFactory.IconSize, icon.Height);
    }

    /// <summary>
    /// The badged states draw strictly more than the healthy one, so they must not be degenerate.
    /// Converting to a bitmap forces the ICO container this factory hand-writes to be parsed by
    /// GDI+ rather than merely produced — a malformed directory entry or a wrong offset shows up
    /// here rather than in the notification area.
    /// </summary>
    [Theory]
    [InlineData(AggregateHealth.Healthy)]
    [InlineData(AggregateHealth.Degraded)]
    [InlineData(AggregateHealth.Error)]
    public void Create_ProducesAnIcoContainer_WindowsCanActuallyDecode(AggregateHealth health)
    {
        var appearance = TrayIconAppearanceSelector.Select(health);

        using var icon = GeneratedIconSourceFactory.Create(appearance);
        using var bitmap = icon.ToBitmap();

        Assert.Equal(GeneratedIconSourceFactory.IconSize, bitmap.Width);
        Assert.Equal(GeneratedIconSourceFactory.IconSize, bitmap.Height);

        // Some pixel is actually painted -- a container that decodes to nothing would satisfy the
        // size assertions above while showing an empty square in the tray.
        var painted = false;
        for (var x = 0; x < bitmap.Width && !painted; x++)
        {
            for (var y = 0; y < bitmap.Height && !painted; y++)
            {
                if (bitmap.GetPixel(x, y).A > 0)
                {
                    painted = true;
                }
            }
        }

        Assert.True(painted, $"The {health} icon decoded to a fully transparent image.");
    }
}
