using System.Windows;
using Bosun.Health;
using Bosun.Rclone.Process;
using Bosun.Supervisor;
using Bosun.Watchdog;
using Microsoft.Extensions.Logging;

namespace Bosun.Repair;

/// <summary>Asks the user to confirm a disruptive repair, and tells them when one failed. Behind an
/// interface so tests never show a dialog.</summary>
public interface IRepairPrompt
{
    /// <summary>True if the user agreed to go ahead. Blocks until they answer.</summary>
    bool Confirm(string title, string message);

    /// <summary>Tells the user a repair could not be done.</summary>
    void ShowError(string title, string message);
}

/// <summary>A plain message box, like <c>MessageBoxDiagnosticsErrorPresenter</c> (the repo's existing
/// pattern for a one-off prompt). Must be called on the UI thread, which a click handler already is.
/// Defaults to "No" so a stray Enter does not disconnect drives.</summary>
public sealed class MessageBoxRepairPrompt : IRepairPrompt
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, $"Bosun - {title}", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, $"Bosun - {title}", MessageBoxButton.OK, MessageBoxImage.Error);
}

/// <summary>
/// The three one-click repairs (bs-aoz, ADR-020 Decision 5): <b>Restart rclone</b>, <b>Unmount all and
/// re-probe</b> and <b>Restart Bosun</b>. One instance serves the tray's Repair menu and the health
/// banner's buttons through <see cref="UI.Tray.HostActionDispatcher"/>, so the two cannot behave
/// differently (ADR-018).
/// </summary>
/// <remarks>
/// <para>
/// <b>What each one touches.</b> None of them calls <c>mount/mount</c> or <c>mount/unmount</c>.
/// <list type="bullet">
/// <item>Restart rclone asks <see cref="IRcloneProcessRestarter"/> for a fresh rcd. The supervisor
/// learns the old mounts are gone from the Healthy transition's reconciliation, never from this class.</item>
/// <item>Unmount all calls <see cref="IMountSupervisor.RepairAllAsync"/> and nothing else on the
/// supervisor. Only the supervisor unmounts.</item>
/// <item>Restart Bosun calls <see cref="IAppRestarter"/> with <see cref="RestartKind.Manual"/>. This class
/// has no reference to the watchdog's restart history, so a manual restart cannot be counted against its
/// hourly limit. That is by construction rather than by a condition somewhere.</item>
/// </list>
/// </para>
/// <para>
/// <b>Double clicks.</b> Each repair has its own in-flight flag; a second request for the same repair
/// while one is running is ignored (logged at Debug). They are separate so that a stuck Unmount all
/// (a stalled supervisor never answers) cannot stop the user using Restart Bosun, which is the repair
/// for exactly that.
/// </para>
/// <para>
/// <b>Threading.</b> Call these on the UI thread, as <c>CopyDiagnosticsCommand</c> is called: the
/// confirmation is a modal box and the continuations after an <c>await</c> resume there. They never
/// throw, so a click handler can fire and forget.
/// </para>
/// </remarks>
public sealed class RepairCommands
{
    /// <summary>
    /// How long "Unmount all" waits for the supervisor before giving up and telling the user. 90 seconds:
    /// a drain that is confirmed returns within the 10 s unmount timeout per host, and one that cannot be
    /// confirmed does not hold the command (it keeps retrying on its own schedule), so anything past this
    /// is a supervisor that is not answering. The watchdog restarts a stalled one after 3 minutes; the
    /// user should not be left looking at a button that does nothing for that long.
    /// </summary>
    public static readonly TimeSpan SupervisorTimeout = TimeSpan.FromSeconds(90);

    private readonly IMountSupervisor supervisor;
    private readonly Func<IRcloneProcessRestarter?> rclone;
    private readonly Func<IAppRestarter?> appRestarter;
    private readonly IRepairPrompt prompt;
    private readonly TimeProvider timeProvider;
    private readonly IAppHealth? health;
    private readonly ILogger<RepairCommands>? logger;

    private int restartingRclone;
    private int unmountingAll;
    private int restartingBosun;

    public RepairCommands(
        IMountSupervisor supervisor,
        Func<IRcloneProcessRestarter?> rclone,
        Func<IAppRestarter?> appRestarter,
        IRepairPrompt prompt,
        TimeProvider timeProvider,
        IAppHealth? health = null,
        ILogger<RepairCommands>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(rclone);
        ArgumentNullException.ThrowIfNull(appRestarter);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.supervisor = supervisor;
        this.rclone = rclone;
        this.appRestarter = appRestarter;
        this.prompt = prompt;
        this.timeProvider = timeProvider;
        this.health = health;
        this.logger = logger;
    }

    public bool IsRestartingRclone => Volatile.Read(ref restartingRclone) == 1;

    public bool IsUnmountingAll => Volatile.Read(ref unmountingAll) == 1;

    public bool IsRestartingBosun => Volatile.Read(ref restartingBosun) == 1;

    /// <summary>Runs the repair for <paramref name="kind"/>. <see cref="RepairActionKind.DismissNotice"/> is
    /// not a repair and is not handled here.</summary>
    public Task RunAsync(RepairActionKind kind) => kind switch
    {
        RepairActionKind.RestartRclone => RestartRcloneAsync(),
        RepairActionKind.UnmountAllAndReprobe => UnmountAllAsync(),
        RepairActionKind.RestartBosun => RestartBosunAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a repair action."),
    };

    /// <summary>Restart rclone: confirms first only if mounted drives would be disconnected.</summary>
    public async Task RestartRcloneAsync()
    {
        if (!TryEnter(ref restartingRclone, "Restart rclone"))
        {
            return;
        }

        try
        {
            var restarter = rclone();
            if (restarter is null)
            {
                logger?.LogWarning("Repair: Restart rclone was requested, but rclone is not being managed (startup did not get as far as starting it)");
                prompt.ShowError(
                    "Restart rclone",
                    "rclone was never started, so there is nothing to restart. Look at the message in the Bosun window for why, then use Restart Bosun.");
                return;
            }

            var mounted = MountedDrives();
            if (mounted.Count > 0 && !prompt.Confirm(
                "Restart rclone",
                $"Restarting rclone disconnects the mounted drive(s) {string.Join(", ", mounted)}. Anything being copied to or from them is interrupted.\n\n" +
                "Bosun reconnects persistent hosts once they pass a fresh check; on-demand hosts stay disconnected until you mount them.\n\nRestart rclone?"))
            {
                logger?.LogInformation("Repair: Restart rclone was cancelled by the user at the confirmation ({Mounted} mounted drive(s))", mounted.Count);
                return;
            }

            logger?.LogInformation(
                "Repair: restarting rclone at the user's request; {Mounted} mounted drive(s) will be dropped and reconciled from scratch. {Health}",
                mounted.Count,
                DescribeHealth());

            var healthy = await restarter.RestartAsync().ConfigureAwait(true);
            if (healthy)
            {
                logger?.LogInformation("Repair: rclone restarted and is healthy; every host is being reconciled from scratch");
            }
            else
            {
                // Not an error dialog: the banner already names the real cause (port held, 401, ...).
                logger?.LogWarning("Repair: rclone was restarted but did not become healthy; the health banner shows why");
            }
        }
        catch (Exception ex)
        {
            Fail("Restart rclone", "Bosun could not restart rclone.", ex);
        }
        finally
        {
            Volatile.Write(ref restartingRclone, 0);
        }
    }

    /// <summary>Unmount all and re-probe. Confirms first if any drive is mounted.</summary>
    public async Task UnmountAllAsync()
    {
        if (!TryEnter(ref unmountingAll, "Unmount all & re-probe"))
        {
            return;
        }

        try
        {
            var mounted = MountedDrives();
            if (mounted.Count > 0 && !prompt.Confirm(
                "Unmount all & re-probe",
                $"This disconnects every mounted drive ({string.Join(", ", mounted)}). Anything being copied to or from them is interrupted.\n\n" +
                "Bosun then checks every host again. Persistent hosts reconnect once they pass the check; on-demand hosts stay disconnected until you mount them. " +
                "A host you unmounted yourself stays unmounted.\n\nUnmount all?"))
            {
                logger?.LogInformation("Repair: Unmount all & re-probe was cancelled by the user at the confirmation ({Mounted} mounted drive(s))", mounted.Count);
                return;
            }

            logger?.LogInformation(
                "Repair: unmount all and re-probe at the user's request; draining {Mounted} mounted drive(s) through the supervisor, then re-probing every host. {Health}",
                mounted.Count,
                DescribeHealth());

            using var timeout = new CancellationTokenSource();
            using var timer = timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(), timeout, SupervisorTimeout, Timeout.InfiniteTimeSpan);

            try
            {
                await supervisor.RepairAllAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                logger?.LogError(
                    "Repair: the supervisor did not accept 'unmount all and re-probe' within {Timeout}; it may be stalled",
                    SupervisorTimeout);
                prompt.ShowError(
                    "Unmount all & re-probe",
                    "Bosun's mount supervisor did not respond, so nothing was unmounted. It may be stalled. Use Restart Bosun.");
                return;
            }

            logger?.LogInformation("Repair: unmount all and re-probe was accepted by the supervisor");
        }
        catch (SupervisorStoppedException ex)
        {
            Fail("Unmount all & re-probe", "Bosun's mount supervisor has stopped, so nothing was unmounted. Use Restart Bosun.", ex);
        }
        catch (Exception ex)
        {
            Fail("Unmount all & re-probe", "Bosun could not unmount and re-probe the hosts.", ex);
        }
        finally
        {
            Volatile.Write(ref unmountingAll, 0);
        }
    }

    /// <summary>Restart Bosun: always confirms. Never touches the watchdog's restart history.</summary>
    public async Task RestartBosunAsync()
    {
        if (!TryEnter(ref restartingBosun, "Restart Bosun"))
        {
            return;
        }

        try
        {
            var restarter = appRestarter();
            if (restarter is null)
            {
                logger?.LogWarning("Repair: Restart Bosun was requested, but no restarter is available");
                prompt.ShowError("Restart Bosun", "Bosun cannot restart itself in this state. Exit it from the tray menu and start it again.");
                return;
            }

            var mounted = MountedDrives();
            var drives = mounted.Count > 0
                ? $"Mounted drives ({string.Join(", ", mounted)}) disconnect when Bosun closes, and persistent hosts reconnect once the new Bosun has checked them. "
                : string.Empty;
            if (!prompt.Confirm(
                "Restart Bosun",
                $"Bosun will close and start again. {drives}Its window opens when it is back.\n\nRestart Bosun?"))
            {
                logger?.LogInformation("Repair: Restart Bosun was cancelled by the user at the confirmation");
                return;
            }

            logger?.LogInformation(
                "Repair: restarting Bosun at the user's request ({Mounted} mounted drive(s)); this is a manual restart and is not counted against the watchdog's limit. {Health}",
                mounted.Count,
                DescribeHealth());

            var started = await restarter.RestartAsync(
                "restart requested by the user from the Repair menu", RestartKind.Manual, CancellationToken.None).ConfigureAwait(true);
            if (!started)
            {
                logger?.LogError("Repair: Restart Bosun failed; the replacement could not be launched and this instance is still running");
                prompt.ShowError(
                    "Restart Bosun",
                    "Bosun could not start a replacement, so it is still running. The log has the reason.");
            }
        }
        catch (Exception ex)
        {
            Fail("Restart Bosun", "Bosun could not restart itself.", ex);
        }
        finally
        {
            Volatile.Write(ref restartingBosun, 0);
        }
    }

    private bool TryEnter(ref int flag, string name)
    {
        if (Interlocked.Exchange(ref flag, 1) == 1)
        {
            logger?.LogDebug("Repair: {Action} is already running; the extra request was ignored", name);
            return false;
        }

        return true;
    }

    private void Fail(string title, string message, Exception ex)
    {
        logger?.LogError(ex, "Repair: {Title} failed", title);
        try
        {
            prompt.ShowError(title, $"{message}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
        }
        catch (Exception promptEx)
        {
            logger?.LogError(promptEx, "Could not show the failure of '{Title}' to the user", title);
        }
    }

    /// <summary>The drive letters currently mounted or mounting, from the supervisor's read-only snapshot.</summary>
    private List<string> MountedDrives()
    {
        try
        {
            return supervisor.GetSnapshot()
                .Where(h => h.State is MountState.Mounted or MountState.Mounting)
                .Select(h => h.Drive ?? h.HostKey)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Repair: could not read the mounted drives from the supervisor snapshot");
            return [];
        }
    }

    private string DescribeHealth()
    {
        var current = health?.Current;
        if (current is null || current.Issues.Count == 0)
        {
            return "Health: OK.";
        }

        return $"Health: {current.Level} ({string.Join(", ", current.Issues.Select(i => i.Code))}).";
    }
}
