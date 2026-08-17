using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bosun.UI.Tray;
using Microsoft.Extensions.Logging;

namespace Bosun.UI;

/// <summary>
/// The real main window (bs-ww9.3, ADR-018). Implements <see cref="IAppWindow"/> so
/// <see cref="MainWindowController"/> can drive show/hide/activate and geometry without knowing
/// about WPF; this class is the thin, deliberately untested (per CLAUDE.md's XAML/drawing
/// carve-out) adapter between the two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition, not construction.</b> The composition root (<c>App.xaml.cs</c>) constructs
/// this with the parameterless constructor <c>InitializeComponent</c> requires, then calls
/// <see cref="Configure"/> once with the real <see cref="IStatusReadModel"/> and
/// <see cref="HostActionDispatcher"/> -- the same two-step "construct, then wire" shape
/// <see cref="MainWindowController"/> itself uses for <see cref="MainWindowController.Initialize"/>.
/// </para>
/// <para>
/// <b>What this window's content deliberately does NOT attempt.</b> ADR-018's stated reasons for
/// a window at all are a readable status table and a scrollable transition log. This delivery
/// builds the table (bound to <see cref="IStatusReadModel.GetRows"/>, refreshed on a timer) but
/// not a transition-log view -- rendering degraded startup state and the transition history
/// causally is bs-ww9.1's job, tracked separately, and out of scope for the app-shell epic this
/// class belongs to.
/// </para>
/// </remarks>
public partial class MainWindow : Window, IAppWindow
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _refreshTimer;
    private IStatusReadModel? _statusReadModel;
    private HostActionDispatcher? _actionDispatcher;

    public ILogger<MainWindow>? Logger { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => RefreshRows();

        HostsGrid.ContextMenuOpening += OnHostsGridContextMenuOpening;
    }

    /// <summary>Wires the real dependencies in and starts the refresh timer. Call once, after
    /// construction and before the window is shown.</summary>
    public void Configure(IStatusReadModel statusReadModel, HostActionDispatcher actionDispatcher)
    {
        _statusReadModel = statusReadModel;
        _actionDispatcher = actionDispatcher;
        RefreshRows();
        _refreshTimer.Start();
    }

    private void RefreshRows()
    {
        if (_statusReadModel is null)
        {
            return;
        }

        try
        {
            var rows = _statusReadModel.GetRows();
            var health = _statusReadModel.GetAggregateHealth();
            HostsGrid.ItemsSource = rows;
            HealthTextBlock.Text = $"Bosun — {health}";
        }
        catch (Exception ex)
        {
            // The window's table going stale for one tick beats an exception escaping a
            // DispatcherTimer.Tick handler, which WPF surfaces as an unhandled dispatcher
            // exception and (per App.xaml.cs's handler) can bring the whole process down.
            Logger?.LogWarning(ex, "Failed to refresh the host status table");
        }
    }

    private void OnHostsGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_actionDispatcher is null)
        {
            e.Handled = true;
            return;
        }

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as HostStatusRow;
        if (row is null)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        foreach (var action in HostContextMenuBuilder.Build(row))
        {
            var item = new MenuItem { Header = action.Label, IsEnabled = action.IsEnabled };
            item.Click += (_, _) => _actionDispatcher.Dispatch(action.Kind, row);
            menu.Items.Add(item);
        }

        HostsGrid.ContextMenu = menu;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------
    // IAppWindow
    // ------------------------------------------------------------------------------------------

    public WindowPlacement GetPlacement()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        var bounds = isMaximized
            ? RestoreBounds
            : new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = isMaximized,
        };
    }

    public void ApplyPlacement(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        WindowState = placement.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    // Explicit implementation: Window.Activate() returns bool (whether the OS honoured the
    // activation request); IAppWindow.Activate() is void -- see that interface's remarks for why
    // the controller has no use for the result.
    void IAppWindow.Activate() => Activate();
}
