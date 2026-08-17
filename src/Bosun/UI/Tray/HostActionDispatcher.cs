using Bosun.Supervisor;
using Microsoft.Extensions.Logging;

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

    public HostActionDispatcher(
        IMountSupervisor supervisor,
        IExternalLauncher launcher,
        ILogger<HostActionDispatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(launcher);

        _supervisor = supervisor;
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches one menu click. Mount/Unmount are fire-and-forget against the supervisor's
    /// command channel -- this method returns immediately; a refusal (e.g.
    /// <c>MountRequestRefusedException</c> while suspended) is logged, not surfaced as a dialog.
    /// The supervisor itself is the authority on whether the action is currently valid (it no-ops
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
                FireAndForget(() => _supervisor.RequestMountAsync(row.HostKey), row.HostKey, "mount");
                break;

            case HostMenuActionKind.Unmount:
                FireAndForget(() => _supervisor.RequestUnmountAsync(row.HostKey), row.HostKey, "unmount");
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

    private void FireAndForget(Func<Task> action, string hostKey, string verb)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to {Verb} {HostKey} from the UI", verb, hostKey);
            }
        }
    }
}
