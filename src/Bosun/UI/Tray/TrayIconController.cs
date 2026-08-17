using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Supervisor;
using Bosun.UI.Autostart;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using Bosun.Status;

namespace Bosun.UI.Tray;

/// <summary>
/// Owns the <c>H.NotifyIcon</c> tray icon (bs-ww9.5, E9b): renders <see cref="AggregateHealth"/>
/// at a glance (ADR-012 Decision 3), builds the per-host context menu, shows/hides the main window,
/// and raises a balloon on an unexpected unmount (ADR-005). Pure UI wiring -- the logic it calls
/// into (<see cref="TrayIconAppearanceSelector"/>, <see cref="HostContextMenuBuilder"/>,
/// <see cref="HostActionDispatcher"/>, <see cref="UnexpectedUnmountDetector"/>) is what carries the
/// unit tests; this class itself is not unit-tested, matching CLAUDE.md's carve-out for XAML and
/// drawing code and the same shape as <c>App.xaml.cs</c> and <c>Bosun.UI.MainWindow</c>.
/// </summary>
/// <remarks>
/// <b>Why polling, not an event.</b> <c>IMountSupervisor</c> exposes no change event by design (see
/// its remarks, and bs-ww9.6's brief: "the supervisor exposes no change event; polling is
/// deliberate and avoids cross-thread marshalling problems"). This class polls
/// <see cref="IStatusReadModel"/> on its own <see cref="DispatcherTimer"/>, already on the UI
/// thread, so no cross-thread marshalling is needed for the icon/menu updates it drives.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    /// <summary>
    /// How often the tray icon and its context menu are rebuilt from
    /// <see cref="IStatusReadModel"/>, and how often the transition history is checked for a new
    /// unexpected unmount. Two seconds: frequent enough that an unmount/remount reads as prompt,
    /// infrequent enough that rebuilding a WPF <see cref="ContextMenu"/> on every tick is not
    /// wasted work for a menu that is open only rarely. Independent of whatever cadence
    /// bs-ww9.6's read-model itself uses internally to poll the supervisor -- this timer only
    /// controls how often THIS class re-reads whatever <see cref="IStatusReadModel"/> currently
    /// reports.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly TaskbarIcon _taskbarIcon;
    private readonly IStatusReadModel _statusReadModel;
    private readonly IMountSupervisor _supervisor;
    private readonly HostActionDispatcher _actionDispatcher;
    private readonly MainWindowController _windowController;
    private readonly IAutostartRegistration _autostart;
    private readonly ILogger<TrayIconController>? _logger;
    private readonly DispatcherTimer _refreshTimer;

    private AggregateHealth? _lastRenderedHealth;
    private DateTimeOffset _lastProcessedTransitionUtc = DateTimeOffset.MinValue;
    private bool _transitionBaselineEstablished;
    private bool _disposed;

    public TrayIconController(
        IStatusReadModel statusReadModel,
        IMountSupervisor supervisor,
        HostActionDispatcher actionDispatcher,
        MainWindowController windowController,
        IAutostartRegistration autostart,
        ILogger<TrayIconController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(statusReadModel);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(actionDispatcher);
        ArgumentNullException.ThrowIfNull(windowController);
        ArgumentNullException.ThrowIfNull(autostart);

        _statusReadModel = statusReadModel;
        _supervisor = supervisor;
        _actionDispatcher = actionDispatcher;
        _windowController = windowController;
        _autostart = autostart;
        _logger = logger;

        _taskbarIcon = new TaskbarIcon();
        _taskbarIcon.TrayLeftMouseUp += (_, _) => _windowController.ShowAndActivate();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refreshTimer.Tick += (_, _) => Refresh();

        Refresh();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        IReadOnlyList<HostStatusRow> rows;
        AggregateHealth health;

        try
        {
            rows = _statusReadModel.Current.Rows;
            health = _statusReadModel.Current.Health;
        }
        catch (Exception ex)
        {
            // The read-model must never take the tray icon down with it -- a stale icon/menu is
            // far better than an exception escaping a DispatcherTimer.Tick handler, which WPF
            // treats as an unhandled dispatcher exception.
            _logger?.LogWarning(ex, "Failed to refresh tray status");
            return;
        }

        UpdateIcon(health);
        UpdateContextMenu(rows);
        CheckForUnexpectedUnmounts(rows);
    }

    private void UpdateIcon(AggregateHealth health)
    {
        if (_lastRenderedHealth == health)
        {
            return;
        }

        var appearance = TrayIconAppearanceSelector.Select(health);
        _taskbarIcon.IconSource = GeneratedIconSourceFactory.Create(appearance);
        _taskbarIcon.ToolTipText = appearance.AccessibleName;
        _lastRenderedHealth = health;
    }

    private void UpdateContextMenu(IReadOnlyList<HostStatusRow> rows)
    {
        var menu = new ContextMenu();

        var toggleItem = new MenuItem
        {
            Header = _windowController.IsWindowVisible ? "Hide window" : "Show window",
        };
        toggleItem.Click += (_, _) => _windowController.ToggleVisibility();
        menu.Items.Add(toggleItem);
        menu.Items.Add(new Separator());

        foreach (var row in rows)
        {
            var hostItem = new MenuItem { Header = row.DisplayName, ToolTip = row.StatusText };

            foreach (var action in HostContextMenuBuilder.Build(row))
            {
                var actionItem = new MenuItem { Header = action.Label, IsEnabled = action.IsEnabled };
                actionItem.Click += (_, _) => _actionDispatcher.Dispatch(action.Kind, row);
                hostItem.Items.Add(actionItem);
            }

            menu.Items.Add(hostItem);
        }

        menu.Items.Add(new Separator());

        // bs-ojc.1: checked state is re-read from IAutostartRegistration every rebuild (this
        // whole method already re-runs on RefreshInterval), never cached across ticks -- a toggle
        // that remembered its own belief instead of reality is precisely the bug the acceptance
        // criteria call out.
        var autostartItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = IsAutostartEnabledSafe(),
        };
        autostartItem.Click += (_, _) => ToggleAutostart(autostartItem.IsChecked);
        menu.Items.Add(autostartItem);

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current?.Shutdown();
        menu.Items.Add(exitItem);

        _taskbarIcon.ContextMenu = menu;
    }

    private bool IsAutostartEnabledSafe()
    {
        try
        {
            return _autostart.IsEnabled();
        }
        catch (Exception ex)
        {
            // Defense in depth: IAutostartRegistration's own contract already guarantees it never
            // throws (ADR-012 fail-soft), but this class wraps every other call into its
            // collaborators the same way (see Refresh() and CheckForUnexpectedUnmounts) rather
            // than trusting a contract silently -- a stale, unchecked toggle is a far smaller
            // problem than a WPF ContextMenu rebuild throwing out of a DispatcherTimer.Tick.
            _logger?.LogWarning(ex, "Failed to read autostart registration state for the tray menu");
            return false;
        }
    }

    private void ToggleAutostart(bool wantEnabled)
    {
        try
        {
            var result = wantEnabled ? _autostart.Enable() : _autostart.Disable();
            if (result == AutostartResult.Failed)
            {
                _logger?.LogWarning(
                    "Failed to {Action} autostart from the tray menu", wantEnabled ? "enable" : "disable");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to toggle autostart from the tray menu");
        }
    }

    /// <summary>
    /// bs-ww9.5: "Balloon notification on UNEXPECTED unmount ... MUST NOT be silent" (ADR-005).
    /// The first call after construction only establishes a baseline timestamp and raises nothing
    /// -- without that, every transition already in the bounded history at startup (including ones
    /// from crash-recovery adoption; see bs-ww9.2) would replay as a fresh balloon the moment
    /// Bosun launches.
    /// </summary>
    private void CheckForUnexpectedUnmounts(IReadOnlyList<HostStatusRow> rows)
    {
        IReadOnlyList<MountTransitionEntry> history;
        try
        {
            history = _supervisor.GetTransitionHistory();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read transition history for unexpected-unmount notifications");
            return;
        }

        if (!_transitionBaselineEstablished)
        {
            _transitionBaselineEstablished = true;
            _lastProcessedTransitionUtc = history.Count > 0 ? history[0].TimestampUtc : DateTimeOffset.MinValue;
            return;
        }

        var newUnexpected = UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts(history, _lastProcessedTransitionUtc);

        foreach (var entry in newUnexpected)
        {
            var displayName = rows.FirstOrDefault(r => r.HostKey == entry.HostKey)?.DisplayName ?? entry.HostKey;
            _taskbarIcon.ShowNotification(
                "Bosun",
                $"{displayName} was unmounted — {entry.Trigger}",
                NotificationIcon.Warning);
        }

        if (history.Count > 0)
        {
            _lastProcessedTransitionUtc = history[0].TimestampUtc;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _taskbarIcon.Dispose();
    }
}
