using Bosun.Tests.UI.Fakes;
using Bosun.UI;

namespace Bosun.Tests.UI;

/// <summary>
/// The show/hide/activate state machine (bs-ww9.3). Every test uses <see cref="FakeAppWindow"/> --
/// never a real <see cref="System.Windows.Window"/> -- per CLAUDE.md's rule that no default-suite
/// test may construct one.
/// </summary>
public sealed class MainWindowControllerTests
{
    private static readonly ScreenBounds Screen = new(0, 0, 1920, 1080);

    private static (MainWindowController Controller, FakeAppWindow Window, FakeWindowPlacementStore Store) CreateSut(
        WindowPlacement? saved = null)
    {
        var window = new FakeAppWindow();
        var store = new FakeWindowPlacementStore(saved);
        var controller = new MainWindowController(window, store, new FakeVirtualScreenProvider(Screen));
        return (controller, window, store);
    }

    [Fact]
    public void Initialize_WithManualLaunchContext_ShowsAndActivatesTheWindow()
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(LaunchContext.Manual);

        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        Assert.Equal(1, window.ShowCallCount);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Fact]
    public void Initialize_WithAutostartLaunchContext_LeavesTheWindowHidden()
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(LaunchContext.Autostart);

        Assert.False(window.IsVisible);
        Assert.False(window.ShowInTaskbar);
        Assert.Equal(0, window.ShowCallCount);
        Assert.Equal(0, window.ActivateCallCount);
    }

    [Fact]
    public void Initialize_AppliesAPlacement_ToTheWindow_EvenWhenNotShown()
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(LaunchContext.Autostart);

        Assert.Equal(1, window.ApplyPlacementCallCount);
    }

    [Fact]
    public void Initialize_WithNoSavedPlacement_AppliesTheDefaultPlacement()
    {
        var (controller, window, _) = CreateSut(saved: null);

        controller.Initialize(LaunchContext.Manual);

        var expected = WindowPlacementClamper.CreateDefault(Screen);
        Assert.Equal(expected, window.GetPlacement());
    }

    [Fact]
    public void Initialize_WithASavedPlacement_AppliesTheClampedPlacement()
    {
        // Off-screen -- as if saved from a monitor layout that no longer exists.
        var saved = new WindowPlacement { Left = 5000, Top = 5000, Width = 800, Height = 600, IsMaximized = false };
        var (controller, window, _) = CreateSut(saved);

        controller.Initialize(LaunchContext.Manual);

        var applied = window.GetPlacement();
        Assert.True(applied.Left + applied.Width <= Screen.Right);
        Assert.True(applied.Top + applied.Height <= Screen.Bottom);
    }

    [Fact]
    public void ShowAndActivate_ShowsTheWindow_GivesItTaskbarPresence_AndActivatesIt()
    {
        var (controller, window, _) = CreateSut();

        controller.ShowAndActivate();

        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Fact]
    public void HideToTray_HidesTheWindow_AndRemovesTaskbarPresence()
    {
        var (controller, window, _) = CreateSut();
        controller.ShowAndActivate();

        controller.HideToTray();

        Assert.False(window.IsVisible);
        Assert.False(window.ShowInTaskbar);
        Assert.Equal(1, window.HideCallCount);
    }

    [Fact]
    public void HideToTray_PersistsTheCurrentPlacement()
    {
        var (controller, window, store) = CreateSut();
        var placement = new WindowPlacement { Left = 10, Top = 20, Width = 700, Height = 500, IsMaximized = false };
        window.SetPlacementForTest(placement);

        controller.HideToTray();

        Assert.Equal(1, store.SaveCallCount);
        Assert.Equal(placement, store.LastSaved);
    }

    [Fact]
    public void ToggleVisibility_HidesAVisibleWindow()
    {
        var (controller, window, _) = CreateSut();
        controller.ShowAndActivate();

        controller.ToggleVisibility();

        Assert.False(window.IsVisible);
    }

    [Fact]
    public void ToggleVisibility_ShowsAHiddenWindow()
    {
        var (controller, window, _) = CreateSut();

        controller.ToggleVisibility();

        Assert.True(window.IsVisible);
    }

    [Fact]
    public void IsWindowVisible_ReflectsTheWindowsCurrentVisibility()
    {
        var (controller, _, _) = CreateSut();

        Assert.False(controller.IsWindowVisible);
        controller.ShowAndActivate();
        Assert.True(controller.IsWindowVisible);
        controller.HideToTray();
        Assert.False(controller.IsWindowVisible);
    }

    [Fact]
    public void PersistCurrentPlacement_SavesTheWindowsCurrentGeometry_WithoutHidingIt()
    {
        var (controller, window, store) = CreateSut();
        controller.ShowAndActivate();

        controller.PersistCurrentPlacement();

        Assert.Equal(1, store.SaveCallCount);
        Assert.True(window.IsVisible);
        Assert.Equal(0, window.HideCallCount);
    }

    [Fact]
    public void Constructor_Throws_ForNullDependencies()
    {
        var window = new FakeAppWindow();
        var store = new FakeWindowPlacementStore();
        var screen = new FakeVirtualScreenProvider(Screen);

        Assert.Throws<ArgumentNullException>(() => new MainWindowController(null!, store, screen));
        Assert.Throws<ArgumentNullException>(() => new MainWindowController(window, null!, screen));
        Assert.Throws<ArgumentNullException>(() => new MainWindowController(window, store, null!));
    }
}
