using Bosun.Hosting;
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

        controller.Initialize(LaunchContext.Manual, ConfigReadinessState.Loaded);

        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        Assert.Equal(1, window.ShowCallCount);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Fact]
    public void Initialize_WithAutostartLaunchContext_LeavesTheWindowHidden()
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.Loaded);

        Assert.False(window.IsVisible);
        Assert.False(window.ShowInTaskbar);
        Assert.Equal(0, window.ShowCallCount);
        Assert.Equal(0, window.ActivateCallCount);
    }

    [Fact]
    public void Initialize_AppliesAPlacement_ToTheWindow_EvenWhenNotShown()
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.Loaded);

        Assert.Equal(1, window.ApplyPlacementCallCount);
    }

    [Fact]
    public void Initialize_WithNoSavedPlacement_AppliesTheDefaultPlacement()
    {
        var (controller, window, _) = CreateSut(saved: null);

        controller.Initialize(LaunchContext.Manual, ConfigReadinessState.Loaded);

        var expected = WindowPlacementClamper.CreateDefault(Screen);
        Assert.Equal(expected, window.GetPlacement());
    }

    [Fact]
    public void Initialize_WithASavedPlacement_AppliesTheClampedPlacement()
    {
        // Off-screen -- as if saved from a monitor layout that no longer exists.
        var saved = new WindowPlacement { Left = 5000, Top = 5000, Width = 800, Height = 600, IsMaximized = false };
        var (controller, window, _) = CreateSut(saved);

        controller.Initialize(LaunchContext.Manual, ConfigReadinessState.Loaded);

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

    // -- bs-ww9.1.3 / ADR-012: first run (no hosts.toml) shows the window whatever the launch context --

    [Theory]
    [InlineData(LaunchContext.Manual, ConfigReadinessState.Loaded, true)]
    [InlineData(LaunchContext.UserRestart, ConfigReadinessState.Loaded, true)]
    [InlineData(LaunchContext.Autostart, ConfigReadinessState.Loaded, false)]
    [InlineData(LaunchContext.WatchdogRestart, ConfigReadinessState.Loaded, false)]
    [InlineData(LaunchContext.Autostart, ConfigReadinessState.Invalid, false)]
    [InlineData(LaunchContext.WatchdogRestart, ConfigReadinessState.Invalid, false)]
    [InlineData(LaunchContext.Manual, ConfigReadinessState.AwaitingFirstRun, true)]
    [InlineData(LaunchContext.UserRestart, ConfigReadinessState.AwaitingFirstRun, true)]
    [InlineData(LaunchContext.Autostart, ConfigReadinessState.AwaitingFirstRun, true)]
    [InlineData(LaunchContext.WatchdogRestart, ConfigReadinessState.AwaitingFirstRun, true)]
    public void ShouldShowOnStartup_ShowsForAskingContexts_AndAlwaysOnFirstRun(
        LaunchContext context, ConfigReadinessState state, bool expected)
    {
        Assert.Equal(expected, MainWindowController.ShouldShowOnStartup(context, state));
    }

    [Theory]
    [InlineData(LaunchContext.Autostart)]
    [InlineData(LaunchContext.WatchdogRestart)]
    public void Initialize_OnFirstRun_ShowsTheWindow_EvenForAQuietLaunchContext(LaunchContext context)
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(context, ConfigReadinessState.AwaitingFirstRun);

        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        Assert.Equal(1, window.ShowCallCount);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Fact]
    public void Initialize_WithAutostartAndAReadyConfig_StaysHidden()
    {
        var (controller, window, _) = CreateSut();

        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.Loaded);

        Assert.False(window.IsVisible);
        Assert.Equal(0, window.ShowCallCount);
    }

    [Fact]
    public void Initialize_WithManual_StillShowsTheWindow_WhateverTheConfigState()
    {
        foreach (var state in Enum.GetValues<ConfigReadinessState>())
        {
            var (controller, window, _) = CreateSut();

            controller.Initialize(LaunchContext.Manual, state);

            Assert.True(window.IsVisible);
            Assert.Equal(1, window.ShowCallCount);
        }
    }

    [Fact]
    public void ReadinessBecomingAwaitingFirstRun_AfterInitialize_ShowsTheHiddenWindowOnce()
    {
        var (controller, window, _) = CreateSut();
        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.Invalid);
        Assert.False(window.IsVisible);

        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);

        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        Assert.Equal(1, window.ShowCallCount);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Fact]
    public void ReadinessRepublished_AsAwaitingFirstRun_NeverShowsOrActivatesTheWindowAgain()
    {
        var (controller, window, _) = CreateSut();
        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.Invalid);
        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);

        // The user closes the window; the orchestrator publishes further snapshots that still say first run.
        controller.HideToTray();
        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);
        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);

        Assert.False(window.IsVisible);
        Assert.Equal(1, window.ShowCallCount);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Fact]
    public void FirstRunShownDuringInitialize_IsNotShownAgainByALaterObservation()
    {
        var (controller, window, _) = CreateSut();
        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.AwaitingFirstRun);
        controller.HideToTray();

        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);

        Assert.False(window.IsVisible);
        Assert.Equal(1, window.ShowCallCount);
    }

    [Fact]
    public void FirstRunObserved_WhileTheWindowIsAlreadyShown_DoesNotActivateItAgain()
    {
        var (controller, window, _) = CreateSut();
        controller.Initialize(LaunchContext.Manual, ConfigReadinessState.Invalid);

        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);

        Assert.Equal(1, window.ShowCallCount);
        Assert.Equal(1, window.ActivateCallCount);
    }

    [Theory]
    [InlineData(ConfigReadinessState.Loaded)]
    [InlineData(ConfigReadinessState.Invalid)]
    public void ObservingAnyOtherConfigState_AfterInitialize_LeavesAHiddenWindowHidden(ConfigReadinessState state)
    {
        var (controller, window, _) = CreateSut();
        controller.Initialize(LaunchContext.Autostart, ConfigReadinessState.Invalid);

        controller.OnConfigStateObserved(state);

        Assert.False(window.IsVisible);
        Assert.Equal(0, window.ShowCallCount);
    }

    [Fact]
    public void ObservingFirstRun_BeforeInitialize_DoesNothing()
    {
        var (controller, window, _) = CreateSut();

        controller.OnConfigStateObserved(ConfigReadinessState.AwaitingFirstRun);

        Assert.False(window.IsVisible);
        Assert.Equal(0, window.ApplyPlacementCallCount);
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
