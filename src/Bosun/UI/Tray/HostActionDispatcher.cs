using Bosun.Diagnostics;
using Bosun.Repair;
using Bosun.Supervisor;
using Bosun.Watchdog;
using Microsoft.Extensions.Logging;
using Bosun.Status;

namespace Bosun.UI.Tray;

/// <summary>
/// The one place a menu click (tray or window, per-host) turns into a call against
/// <see cref="IMountSupervisor"/> or <see cref="IExternalLauncher"/>. Shared by
/// <see cref="TrayIconController"/> and <c>Bosun.UI.MainWindow</c>'s row context menu so the two
/// surfaces cannot drift into different behaviour for the same click (ADR-018's "two surfaces can
/// now trigger the same action... both post commands to the supervisor channel").
/// </summary>
/// <remarks>
/// <b>Hard rule this class exists to uphold (Invariant I4 / ARCHITECTURE.md §3):</b> this class
/// never calls <c>IRcloneClient</c> and never mutates supervisor state directly -- Mount/Unmount
/// go through <see cref="IMountSupervisor"/>'s command methods only, which are the sole caller of
/// <c>mount/mount</c>/<c>mount/unmount</c>. If a future action needs anything more than what
/// <see cref="IMountSupervisor"/> and <see cref="IExternalLauncher"/> already expose, that is an
/// architecture question -- stop and ask, per this project's standing rule for UI code.
/// </remarks>
public sealed class HostActionDispatcher
{
    private readonly IMountSupervisor _supervisor;
    private readonly IExternalLauncher _launcher;
    private readonly ILogger<HostActionDispatcher>? _logger;
    private readonly CopyDiagnosticsCommand? _diagnostics;
    private readonly RepairCommands? _repair;
    private readonly IRestartNotice? _restartNotice;
    private readonly IRepairPrompt? _prompt;

    public HostActionDispatcher(
        IMountSupervisor supervisor,
        IExternalLauncher launcher,
        ILogger<HostActionDispatcher>? logger = null,
        CopyDiagnosticsCommand? diagnostics = null,
        RepairCommands? repair = null,
        IRestartNotice? restartNotice = null,
        IRepairPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(launcher);

        _supervisor = supervisor;
        _launcher = launcher;
        _logger = logger;
        _diagnostics = diagnostics;
        _repair = repair;
        _restartNotice = restartNotice;
        _prompt = prompt;
    }

    /// <summary>
    /// A repair action (bs-aoz, ADR-020 Decision 5): Restart rclone, Unmount all and re-probe, or
    /// Restart Bosun. The one path both the tray's Repair menu and the health banner's buttons use, so
    /// they cannot drift. Returns immediately; <see cref="RepairCommands"/> confirms, guards against a
    /// second click while it is running, and reports its own failure. Nothing here mounts or unmounts:
    /// Unmount all goes through <see cref="IMountSupervisor.RepairAllAsync"/>.
    /// </summary>
    public void Repair(RepairActionKind kind)
    {
        if (kind == RepairActionKind.DismissNotice)
        {
            DismissRestartNotice();
            return;
        }

        if (_repair is null)
        {
            _logger?.LogWarning("Repair action {Kind} was requested but no repair commands are configured", kind);
            return;
        }

        _ = _repair.RunAsync(kind);
    }

    /// <summary>Dismisses the "Bosun restarted itself" notice (bs-aoz).</summary>
    public void DismissRestartNotice()
    {
        if (_restartNotice is null)
        {
            _logger?.LogWarning("Dismiss was requested but no restart notice is configured");
            return;
        }

        _restartNotice.Dismiss();
    }

    /// <summary>
    /// "Copy diagnostics" (bs-ds3): the one application-level command on this path, shared by the
    /// tray menu and the window button for the same reason the per-host actions are. Returns
    /// immediately; the command reports its own failure to the user. It reads state and writes a
    /// zip, and never touches a mount.
    /// </summary>
    public void CopyDiagnostics()
    {
        if (_diagnostics is null)
        {
            _logger?.LogWarning("Copy diagnostics was requested but no diagnostics command is configured");
            return;
        }

        _ = _diagnostics.RunAsync();
    }

    /// <summary>
    /// Dispatches one menu click. Mount/Unmount are fire-and-forget against the supervisor's
    /// command channel -- this method returns immediately. A refusal (e.g.
    /// <c>MountRequestRefusedException</c> while suspended) is shown to the user in the app's one
    /// dialog (bs-ngo, <see cref="HostActionFailure"/>), and any other failure is logged with a generic
    /// message shown. The supervisor itself is the authority on whether the action is currently valid (it no-ops
    /// a Mount/Unmount call that does not apply to the host's current state) --
    /// <see cref="HostContextMenuBuilder"/> only decides whether to let the user click in the
    /// first place; this method does not re-check <see cref="HostMenuAction.IsEnabled"/>.
    /// </summary>
    public void Dispatch(HostMenuActionKind kind, HostStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        switch (kind)
        {
            case HostMenuActionKind.Mount:
                FireAndForget(() => _supervisor.RequestMountAsync(row.HostKey), row, isMount: true);
                break;

            case HostMenuActionKind.Unmount:
                FireAndForget(() => _supervisor.RequestUnmountAsync(row.HostKey), row, isMount: false);
                break;

            case HostMenuActionKind.OpenTerminal:
                _launcher.OpenTerminal(row.HostKey);
                break;

            case HostMenuActionKind.OpenInExplorer:
                if (row.Drive is { } drive)
                {
                    _launcher.OpenInExplorer(drive);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled HostMenuActionKind value.");
        }
    }

    /// <remarks>
    /// <b>Why the dialog and not a tray balloon (bs-ngo).</b> A refusal answers a click the user just
    /// made, so it must be seen and must stay until read. A balloon is neither: Focus Assist swallows
    /// it, and it times out. <see cref="IRepairPrompt.ShowError"/> is the path Copy diagnostics and
    /// the repairs already use (bs-6bc, bs-3hx): it is queued until the menu that raised the click has
    /// closed, so the dialog is not destroyed with the menu, and it is owned by the main window.
    /// Balloons stay for things nobody clicked for (an unexpected unmount, a fault). The cost is that
    /// the main window is brought forward to own the dialog, which a repair dialog does too.
    /// </remarks>
    private void FireAndForget(Func<Task> action, HostStatusRow row, bool isMount)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // Bosun shutting down cancels in-flight requests. Not a failure to report.
                _logger?.LogDebug(ex, "The {Verb} request for {HostKey} was cancelled", isMount ? "mount" : "unmount", row.HostKey);
            }
            catch (Exception ex)
            {
                var failure = HostActionFailure.Describe(ex, row.DisplayName, isMount);

                if (failure.IsExpectedRefusal)
                {
                    _logger?.LogInformation(
                        "The {Verb} request for {HostKey} was refused: {Reason}",
                        isMount ? "mount" : "unmount", row.HostKey, ex.Message);
                }
                else
                {
                    _logger?.LogWarning(ex, "Failed to {Verb} {HostKey} from the UI", isMount ? "mount" : "unmount", row.HostKey);
                }

                TellTheUser(failure);
            }
        }
    }

    private void TellTheUser(HostActionFailure failure)
    {
        if (_prompt is null)
        {
            _logger?.LogWarning("No dialog prompt is configured, so this was not shown to the user: {Message}", failure.Message);
            return;
        }

        try
        {
            _prompt.ShowError(failure.Title, failure.Message);
        }
        catch (Exception ex)
        {
            // ShowError is meant not to throw; this is the last place anything could still be said.
            _logger?.LogError(ex, "Could not show the '{Title}' dialog to the user", failure.Title);
        }
    }
}
