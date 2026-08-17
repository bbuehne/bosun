using System.Windows;

namespace Bosun.UI;

/// <summary>
/// The reachable virtual desktop, in device-independent WPF units -- the union of every attached
/// monitor. Deliberately not <c>System.Drawing.Rectangle</c>/<c>System.Windows.Forms.Screen</c>:
/// this project has no WinForms dependency, and WPF's own coordinate space is what
/// <see cref="System.Windows.Window.Left"/>/<see cref="System.Windows.Window.Top"/> are expressed
/// in.
/// </summary>
public readonly record struct ScreenBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Seam over <see cref="SystemParameters"/>'s virtual-screen properties, so
/// <see cref="WindowPlacementClamper"/> and <see cref="MainWindowController"/> are testable
/// against an arbitrary monitor layout (including "the monitor the window was last on is now
/// unplugged") without touching real display hardware.
/// </summary>
public interface IVirtualScreenProvider
{
    ScreenBounds GetVirtualScreenBounds();
}

/// <summary>
/// Real <see cref="IVirtualScreenProvider"/>. Reading <see cref="SystemParameters"/> does not
/// require a live <see cref="System.Windows.Window"/> or <see cref="System.Windows.Application"/>
/// to be constructed -- it is safe to construct and call from a worktree -- but it does reflect
/// the real, current monitor layout, so it is not used by any default-suite test (those use a
/// fake with a fixed, known layout instead).
/// </summary>
public sealed class WpfVirtualScreenProvider : IVirtualScreenProvider
{
    public ScreenBounds GetVirtualScreenBounds() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);
}
