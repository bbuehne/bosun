using Bosun.UI;

namespace Bosun.Tests.UI.Fakes;

/// <summary>Records calls instead of touching a real WPF <see cref="System.Windows.Window"/>,
/// per CLAUDE.md's rule that no default-suite test may construct one.</summary>
internal sealed class FakeAppWindow : IAppWindow
{
    private WindowPlacement _placement = new()
    {
        Left = 0,
        Top = 0,
        Width = WindowPlacementClamper.DefaultWidth,
        Height = WindowPlacementClamper.DefaultHeight,
        IsMaximized = false,
    };

    public bool IsVisible { get; private set; }
    public bool ShowInTaskbar { get; set; }

    public int ShowCallCount { get; private set; }
    public int HideCallCount { get; private set; }
    public int ActivateCallCount { get; private set; }
    public int ApplyPlacementCallCount { get; private set; }

    public WindowPlacement GetPlacement() => _placement;

    public void ApplyPlacement(WindowPlacement placement)
    {
        _placement = placement;
        ApplyPlacementCallCount++;
    }

    public void Show()
    {
        IsVisible = true;
        ShowCallCount++;
    }

    public void Hide()
    {
        IsVisible = false;
        HideCallCount++;
    }

    public void Activate() => ActivateCallCount++;

    /// <summary>Lets a test simulate the window's geometry having changed (a drag/resize) before
    /// the next <see cref="MainWindowController.PersistCurrentPlacement"/>/<see cref="MainWindowController.HideToTray"/> call.</summary>
    public void SetPlacementForTest(WindowPlacement placement) => _placement = placement;
}
