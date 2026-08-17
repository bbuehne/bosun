namespace Bosun.UI;

/// <summary>
/// The slice of <see cref="System.Windows.Window"/> that <see cref="MainWindowController"/>
/// needs, so the show/hide/activate state machine and window-state persistence are unit-testable
/// without constructing a real WPF <see cref="System.Windows.Window"/> (CLAUDE.md: "No
/// default-suite test may construct a WPF Window or Application"). <see cref="MainWindow"/> is the
/// real implementation; tests use a fake.
/// </summary>
public interface IAppWindow
{
    /// <summary>Mirrors <see cref="System.Windows.UIElement.IsVisible"/>.</summary>
    bool IsVisible { get; }

    /// <summary>
    /// Mirrors <see cref="System.Windows.Window.ShowInTaskbar"/>. Owned by the controller, not the
    /// window itself: ADR-018 rule 4, "taskbar presence only while the window is shown" -- the
    /// controller sets this to <see langword="true"/> exactly when it shows the window and
    /// <see langword="false"/> exactly when it hides it, so the two can never drift apart.
    /// </summary>
    bool ShowInTaskbar { get; set; }

    /// <summary>Current geometry, expressed as restored (non-maximized) bounds plus a maximized
    /// flag -- see <see cref="WindowPlacement"/>'s doc comment.</summary>
    WindowPlacement GetPlacement();

    /// <summary>Applies a (already-clamped) placement to the window.</summary>
    void ApplyPlacement(WindowPlacement placement);

    void Show();
    void Hide();

    /// <summary>Brings the window to the foreground. <see langword="void"/> rather than
    /// <see cref="System.Windows.Window.Activate"/>'s <see cref="bool"/> return -- the controller
    /// has no different behaviour for the "activation request was denied by the OS" case, so
    /// there is nothing useful to do with the result.</summary>
    void Activate();
}
