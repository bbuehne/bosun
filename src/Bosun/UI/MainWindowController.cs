using Bosun.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.UI;

/// <summary>
/// The show/hide/activate state machine for the main window (bs-ww9.3, ADR-018). Operates purely
/// against <see cref="IAppWindow"/>, <see cref="IWindowPlacementStore"/>, and
/// <see cref="IVirtualScreenProvider"/> -- never a real <see cref="System.Windows.Window"/> -- so
/// it is unit-testable without WPF, following the same extraction pattern
/// <see cref="Hosting.BootstrapOrchestrator"/> already established for exactly this reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who calls what.</b> The composition root (<c>App.xaml.cs</c>) owns the real
/// <see cref="System.Windows.Window"/> and wires its native events to this controller: the
/// window's <c>Closing</c> event calls <see cref="HideToTray"/> (and cancels the close), the tray
/// icon's left-click and "Show" menu item call <see cref="ShowAndActivate"/>, and
/// <c>SingleInstanceOrchestrator.ActivationRequested</c> (marshalled to the UI thread) also calls
/// <see cref="ShowAndActivate"/>. This class knows nothing about the tray, single-instance
/// activation, or WPF's <c>CancelEventArgs</c> -- it only knows show/hide/activate and placement.
/// </para>
/// <para>
/// <b>Why <see cref="Initialize"/> does not always call <see cref="ShowAndActivate"/>.</b>
/// ADR-018 rule 2 is the entire reason this class exists as a separate seam from ordinary WPF
/// window plumbing: launched at login (<see cref="LaunchContext.Autostart"/>), the window must
/// stay hidden and out of the taskbar. Launched by hand
/// (<see cref="LaunchContext.Manual"/>), it must show immediately. The one override is first run
/// (no <c>hosts.toml</c>, <see cref="ConfigReadinessState.AwaitingFirstRun"/>): ADR-012 says it is a
/// window shown actively, so it shows under every launch context (bs-ww9.1.3).
/// </para>
/// </remarks>
public sealed class MainWindowController
{
    private readonly IAppWindow _window;
    private readonly IWindowPlacementStore _store;
    private readonly IVirtualScreenProvider _screenProvider;
    private readonly ILogger<MainWindowController>? _logger;
    private bool _initialized;
    private bool _firstRunHandled;

    public MainWindowController(
        IAppWindow window,
        IWindowPlacementStore store,
        IVirtualScreenProvider screenProvider,
        ILogger<MainWindowController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(screenProvider);

        _window = window;
        _store = store;
        _screenProvider = screenProvider;
        _logger = logger;
    }

    /// <summary>Whether the window is currently shown. Read by <c>TrayIconController</c> to label
    /// its "Show window"/"Hide window" toggle menu item correctly.</summary>
    public bool IsWindowVisible => _window.IsVisible;

    /// <summary>
    /// Whether the window is shown when Bosun starts (ADR-018 rule 2, ADR-012 Decision 2). True when
    /// the launch context asks for a window (<see cref="LaunchContext.Manual"/>,
    /// <see cref="LaunchContext.UserRestart"/>), and also whenever there is no <c>hosts.toml</c> yet
    /// (<see cref="ConfigReadinessState.AwaitingFirstRun"/>), whatever the launch context: a first run
    /// started at login or by the watchdog has nothing else to show the user (bs-ww9.1.3).
    /// </summary>
    public static bool ShouldShowOnStartup(LaunchContext context, ConfigReadinessState configState) =>
        context.ShowsWindowAtStartup() || configState == ConfigReadinessState.AwaitingFirstRun;

    /// <summary>Loads (and clamps -- see <see cref="WindowPlacementClamper"/>) the persisted
    /// placement, applies it to the window, and shows the window if
    /// <see cref="ShouldShowOnStartup"/> says so: the launch context is
    /// <see cref="LaunchContext.Manual"/> or <see cref="LaunchContext.UserRestart"/> (ADR-018 rule 2;
    /// an autostart or watchdog restart stays hidden, bs-aoz), or <paramref name="configState"/> is
    /// <see cref="ConfigReadinessState.AwaitingFirstRun"/> (bs-ww9.1.3). Call exactly once, before any
    /// other method on this class.</summary>
    /// <param name="configState">The config readiness known when the UI is built. It may not be final:
    /// if it is not <see cref="ConfigReadinessState.AwaitingFirstRun"/> yet, a later change is
    /// reported through <see cref="OnConfigStateObserved"/>.</param>
    public void Initialize(LaunchContext context, ConfigReadinessState configState)
    {
        var virtualScreen = _screenProvider.GetVirtualScreenBounds();
        var saved = _store.TryLoad();
        var placement = saved is null
            ? WindowPlacementClamper.CreateDefault(virtualScreen)
            : WindowPlacementClamper.Clamp(saved, virtualScreen);

        _window.ApplyPlacement(placement);
        _initialized = true;

        if (configState == ConfigReadinessState.AwaitingFirstRun)
        {
            // Whatever shows the window below, the first-run window has now had its turn.
            _firstRunHandled = true;
        }

        if (ShouldShowOnStartup(context, configState))
        {
            ShowAndActivate();
        }

        _logger?.LogInformation(
            "Main window initialized for launch context {LaunchContext}, config state {ConfigState}", context, configState);
    }

    /// <summary>
    /// Tells the controller the config readiness changed after <see cref="Initialize"/> (the startup
    /// sequence publishes it as it goes). The first time it becomes
    /// <see cref="ConfigReadinessState.AwaitingFirstRun"/> the window is shown if it is not already;
    /// every later call, and every other state, does nothing. At most once per process, so a
    /// readiness republish can never pull the window back over what the user is doing, and a window
    /// the user closed stays closed. Must be called on the UI thread.
    /// </summary>
    public void OnConfigStateObserved(ConfigReadinessState configState)
    {
        if (!_initialized || _firstRunHandled || configState != ConfigReadinessState.AwaitingFirstRun)
        {
            return;
        }

        _firstRunHandled = true;
        if (!_window.IsVisible)
        {
            _logger?.LogInformation("No hosts.toml yet; showing the main window for first run");
            ShowAndActivate();
        }
    }

    /// <summary>Shows the window, gives it a taskbar presence (ADR-018 rule 4), and brings it to
    /// the foreground. Idempotent -- calling this on an already-visible window is harmless.</summary>
    public void ShowAndActivate()
    {
        _window.ShowInTaskbar = true;
        _window.Show();
        _window.Activate();
    }

    /// <summary>Persists the current placement, then hides the window and removes its taskbar
    /// entry (ADR-018 rules 3 and 4). This is what the window's Close button does -- it never
    /// exits the process.</summary>
    public void HideToTray()
    {
        PersistCurrentPlacement();
        _window.ShowInTaskbar = false;
        _window.Hide();
    }

    /// <summary>Shows the window if hidden, hides it if shown. Used by the tray icon's
    /// double-click, where "toggle" is the more natural gesture than always-show.</summary>
    public void ToggleVisibility()
    {
        if (_window.IsVisible)
        {
            HideToTray();
        }
        else
        {
            ShowAndActivate();
        }
    }

    /// <summary>Reads the window's current geometry and saves it. Called from
    /// <see cref="HideToTray"/>, and also by the composition root on process exit if the window is
    /// still visible at that point (a user who exits via the tray's "Exit" item while the window
    /// is open never goes through <see cref="HideToTray"/>, so nothing else would persist that
    /// final geometry).</summary>
    public void PersistCurrentPlacement()
    {
        _store.Save(_window.GetPlacement());
    }
}
