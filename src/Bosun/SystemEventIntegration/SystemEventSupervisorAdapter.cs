using Bosun.Configuration;
using Bosun.Supervisor;
using Microsoft.Extensions.Logging;

namespace Bosun.SystemEventIntegration;

/// <summary>
/// Translates <see cref="ISystemEventSource"/> events into <see cref="IMountSupervisor"/> calls
/// (bs-ohk). This is an ADAPTER, not new state-machine logic: <see cref="IMountSupervisor.SuspendAsync"/>,
/// <see cref="IMountSupervisor.ResumeAsync"/>, and <see cref="IMountSupervisor.NetworkChangedAsync"/>
/// already implement everything docs/ARCHITECTURE.md §4 rule 5 and the Backoff section's reset
/// triggers require (E5); this class only wires the OS events to them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Suspend is the one path that blocks (Invariant I8).</b> Windows gives roughly two seconds
/// between raising <c>PowerModeChanged(Suspend)</c> and proceeding with suspend regardless of
/// whether the handler has returned (docs/ARCHITECTURE.md §3; <c>bs-ajh</c>). Returning immediately
/// (fire-and-forget, like <see cref="OnResume"/>/<see cref="OnNetworkAddressChanged"/> below) would
/// give the drain no window to run before the machine sleeps anyway, which defeats the entire point
/// of subscribing to this event -- so <see cref="OnSuspend"/> deliberately blocks the calling thread,
/// bounded by <c>global.suspend_unmount_timeout_seconds</c>, via <see cref="WaitForSuspendDrainAsync"/>.
/// </para>
/// <para>
/// <b>Why this cannot deadlock even when the supervisor's channel is busy.</b>
/// <see cref="IMountSupervisor.SuspendAsync"/> posts to the same single command channel every other
/// supervisor call uses (docs/ARCHITECTURE.md §5) and does not complete until that specific
/// continuation has been dequeued AND fully run. If a slow drain for some other host is already
/// in flight -- or simply queued ahead of this call -- <c>SuspendAsync</c>'s returned
/// <see cref="Task"/> will not complete until that finishes, which could be far longer than the
/// suspend budget. <see cref="WaitForSuspendDrainAsync"/> races that task against
/// <c>Task.Delay(budget, timeProvider)</c> with <see cref="Task.WhenAny(Task, Task)"/> instead of
/// simply <c>await</c>-ing it, so this method returns the moment the budget elapses regardless of
/// what the channel is doing. The in-flight <c>SuspendAsync</c> call is never cancelled or
/// abandoned when that happens -- it keeps running and will still drain (or force-unmount) in the
/// background; this method only stops <i>waiting</i> for it, and logs that outcome explicitly
/// rather than claiming the drain completed.
/// </para>
/// <para>
/// <b>Resume, network-change, and session-lock do not block.</b> There is no OS-imposed deadline
/// for them, and <see cref="IMountSupervisor.NetworkChangedAsync"/> in particular can take a while
/// (it force-probes every idle and every mounted host in one channel action) -- blocking
/// <c>SystemEvents</c>' one dedicated thread for that long would delay delivery of any other
/// event (including a subsequent <see cref="ISystemEventSource.Suspend"/>) to every subscriber in
/// the process, not just this one. These three are therefore fire-and-forget: the supervisor call
/// is issued and its eventual fault (if any) is logged via a continuation, but nothing here waits
/// for it.
/// </para>
/// <para>
/// <b>Trigger logging.</b> Every log line this class emits is prefixed "Trigger: system ...
/// event" specifically so it reads unambiguously as the system-event path when read after the
/// fact -- docs/OPERATIONS.md's entire T1-T3 triage story depends on being able to tell a
/// suspend/resume/network-change-driven transition apart from a user action or a routine probe
/// failure in the supervisor's own transition log.
/// </para>
/// </remarks>
public sealed class SystemEventSupervisorAdapter : IDisposable
{
    private readonly ISystemEventSource eventSource;
    private readonly IMountSupervisor supervisor;
    private readonly IHostConfigStore configStore;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemEventSupervisorAdapter> logger;

    private bool disposed;

    public SystemEventSupervisorAdapter(
        ISystemEventSource eventSource,
        IMountSupervisor supervisor,
        IHostConfigStore configStore,
        TimeProvider timeProvider,
        ILogger<SystemEventSupervisorAdapter> logger)
    {
        this.eventSource = eventSource;
        this.supervisor = supervisor;
        this.configStore = configStore;
        this.timeProvider = timeProvider;
        this.logger = logger;

        eventSource.Suspend += OnSuspend;
        eventSource.Resume += OnResume;
        eventSource.NetworkAddressChanged += OnNetworkAddressChanged;
        eventSource.SessionLock += OnSessionLock;
    }

    // ------------------------------------------------------------------------------------------
    // Suspend (Invariant I8) -- see the class remarks.
    // ------------------------------------------------------------------------------------------

    private void OnSuspend(object? sender, EventArgs e)
    {
        // Blocking here is intentional -- see the class remarks. Every awaited call in the chain
        // below uses ConfigureAwait(false), so there is no captured context to marshal back to and
        // this cannot deadlock against itself.
        WaitForSuspendDrainAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The whole of Invariant I8's runtime enforcement. Issues <see cref="IMountSupervisor.SuspendAsync"/>
    /// and never waits longer than <c>global.suspend_unmount_timeout_seconds</c> for it to confirm --
    /// see the class remarks for why a race against a budget-length delay, rather than a plain
    /// <c>await</c>, is what makes this safe when the supervisor's command channel is busy. Returns
    /// <see langword="true"/> if the drain confirmed within budget, <see langword="false"/> if the
    /// budget elapsed first (logged as a warning either way it happens, never silently).
    /// </summary>
    internal async Task<bool> WaitForSuspendDrainAsync(CancellationToken cancellationToken = default)
    {
        var budgetSeconds = configStore.Current.Global.SuspendUnmountTimeoutSeconds;
        var budget = TimeSpan.FromSeconds(budgetSeconds);

        logger.LogInformation(
            "Trigger: system suspend event -- issuing SuspendAsync (Invariant I8 budget: {BudgetSeconds}s)",
            budgetSeconds);

        var supervisorTask = supervisor.SuspendAsync(cancellationToken);
        var winner = await Task.WhenAny(supervisorTask, Task.Delay(budget, timeProvider, cancellationToken))
            .ConfigureAwait(false);

        if (ReferenceEquals(winner, supervisorTask))
        {
            // Observe the outcome WITHOUT letting a fault propagate: this method is called
            // synchronously from OnSuspend, which runs on Microsoft.Win32.SystemEvents' dedicated
            // notification thread in production. There is no catch above that call -- an unhandled
            // exception there terminates the process (bs-3al), and a dead process never runs
            // resume-side reconciliation, which is the exact wedged-drive outcome I8 exists to
            // prevent. The sibling handlers (OnResume, OnNetworkAddressChanged) already log faults
            // instead of throwing via FireAndForget; this brings suspend in line with them rather
            // than being the one path that still propagates.
            if (supervisorTask.IsFaulted)
            {
                logger.LogError(
                    supervisorTask.Exception,
                    "Trigger: system suspend event -- the drain faulted within the {BudgetSeconds}s budget",
                    budgetSeconds);
            }
            else
            {
                logger.LogInformation(
                    "Trigger: system suspend event -- drain confirmed within the {BudgetSeconds}s budget", budgetSeconds);
            }

            return true;
        }

        logger.LogWarning(
            "Trigger: system suspend event -- the {BudgetSeconds}s suspend budget (Invariant I8) elapsed before " +
            "the drain confirmed; returning control to Windows now rather than risk exceeding the window it " +
            "allows. A forced unmount may still be needed -- the in-flight drain keeps running in the " +
            "background, and resume-time reconciliation will catch anything left over.",
            budgetSeconds);

        // The in-flight call is deliberately left running (see class remarks) -- observe its
        // eventual outcome so a later fault surfaces in the log instead of as an unobserved task
        // exception; nothing is still awaiting it by the time it completes.
        _ = supervisorTask.ContinueWith(
            t => logger.LogError(
                t.Exception, "Trigger: system suspend event -- the drain issued past the budget eventually faulted"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return false;
    }

    // ------------------------------------------------------------------------------------------
    // Resume / network change / session lock -- fire-and-forget, see class remarks.
    // ------------------------------------------------------------------------------------------

    private void OnResume(object? sender, EventArgs e)
    {
        logger.LogInformation("Trigger: system resume event -- issuing ResumeAsync");
        FireAndForget(supervisor.ResumeAsync(), "system resume event");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        logger.LogInformation("Trigger: network address change event -- issuing NetworkChangedAsync");
        FireAndForget(supervisor.NetworkChangedAsync(), "network address change event");
    }

    private void OnSessionLock(object? sender, EventArgs e)
    {
        // v1: reserved. Deliberately calls nothing on IMountSupervisor -- see docs/ARCHITECTURE.md
        // §3 and the class remarks.
        logger.LogInformation("Trigger: session lock event -- no action taken (v1, reserved)");
    }

    private void FireAndForget(Task task, string triggerLabel)
    {
        _ = task.ContinueWith(
            t => logger.LogError(t.Exception, "Trigger: {TriggerLabel} -- supervisor call faulted", triggerLabel),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        eventSource.Suspend -= OnSuspend;
        eventSource.Resume -= OnResume;
        eventSource.NetworkAddressChanged -= OnNetworkAddressChanged;
        eventSource.SessionLock -= OnSessionLock;
    }
}
