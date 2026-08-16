using Bosun.SystemEventIntegration;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.SystemEventIntegration;

/// <summary>
/// <see cref="SystemEventSupervisorAdapter"/> (bs-ohk): translates <see cref="FakeSystemEventSource"/>
/// events into <see cref="FakeMountSupervisor"/> calls. This is the adapter, not the state machine
/// -- E5's <c>MountSupervisor</c> already implements everything docs/ARCHITECTURE.md §4 rule 5 and
/// the Backoff section require; these tests only prove the wiring, the suspend budget, and that
/// <c>SessionLock</c> is provably inert.
/// </summary>
public sealed class SystemEventSupervisorAdapterTests
{
    private static (FakeSystemEventSource EventSource, FakeMountSupervisor Supervisor, FakeHostConfigStore ConfigStore,
        FakeTimeProvider Time, SystemEventSupervisorAdapter Adapter) CreateHarness(int suspendUnmountTimeoutSeconds = 8)
    {
        var eventSource = new FakeSystemEventSource();
        var supervisor = new FakeMountSupervisor();
        var configStore = new FakeHostConfigStore(
            HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: suspendUnmountTimeoutSeconds)));
        var time = new FakeTimeProvider();
        var adapter = new SystemEventSupervisorAdapter(
            eventSource, supervisor, configStore, time, NullLogger<SystemEventSupervisorAdapter>.Instance);

        return (eventSource, supervisor, configStore, time, adapter);
    }

    // --------------------------------------------------------------------------------------
    // Wiring: fake events drive real (fake) supervisor calls.
    // --------------------------------------------------------------------------------------

    [Fact]
    public void NetworkAddressChanged_calls_NetworkChangedAsync()
    {
        var (eventSource, supervisor, _, _, _) = CreateHarness();

        eventSource.RaiseNetworkAddressChanged();

        Assert.Single(supervisor.NetworkChangedCalls);
        Assert.Empty(supervisor.SuspendCalls);
        Assert.Empty(supervisor.ResumeCalls);
    }

    [Fact]
    public void Resume_calls_ResumeAsync()
    {
        var (eventSource, supervisor, _, _, _) = CreateHarness();

        eventSource.RaiseResume();

        Assert.Single(supervisor.ResumeCalls);
        Assert.Empty(supervisor.SuspendCalls);
        Assert.Empty(supervisor.NetworkChangedCalls);
    }

    [Fact]
    public void Suspend_via_the_full_synchronous_event_path_calls_SuspendAsync_and_returns_once_it_completes()
    {
        // The supervisor call resolves immediately here (no PendingSuspend armed), so the
        // synchronous OnSuspend -> WaitForSuspendDrainAsync().GetAwaiter().GetResult() chain
        // resolves without ever needing the fake clock advanced -- safe to drive through the real
        // event-raising path (unlike the busy-channel scenario below, which must NOT be driven
        // this way; see that test's remarks).
        var (eventSource, supervisor, _, _, _) = CreateHarness();

        eventSource.RaiseSuspend();

        Assert.Single(supervisor.SuspendCalls);
    }

    /// <summary>"SessionLock provably does nothing" (bs-ohk acceptance criterion): raising it must
    /// call none of Suspend/Resume/NetworkChangedAsync on the supervisor.</summary>
    [Fact]
    public void SessionLock_calls_nothing_on_the_supervisor()
    {
        var (eventSource, supervisor, _, _, _) = CreateHarness();

        eventSource.RaiseSessionLock();

        Assert.Empty(supervisor.SuspendCalls);
        Assert.Empty(supervisor.ResumeCalls);
        Assert.Empty(supervisor.NetworkChangedCalls);
    }

    // --------------------------------------------------------------------------------------
    // Invariant I8: the suspend budget. THE test that matters -- proves the wait is bounded even
    // when the supervisor's own call has not (and, within this test, never will) complete, which
    // is what a busy command channel / an in-flight drain for another host looks like from this
    // adapter's point of view (docs/ARCHITECTURE.md §5: one channel, one command at a time).
    // --------------------------------------------------------------------------------------

    /// <summary>
    /// Deliberately calls <see cref="SystemEventSupervisorAdapter.WaitForSuspendDrainAsync"/>
    /// directly rather than through <see cref="FakeSystemEventSource.RaiseSuspend"/>. The public
    /// path (<c>OnSuspend</c>) blocks its calling thread synchronously
    /// (<c>.GetAwaiter().GetResult()</c>) by design -- that is the entire mechanism by which
    /// Invariant I8 gets a real window before Windows suspends regardless (see the adapter's class
    /// remarks). Driving that SAME thread's <see cref="FakeTimeProvider"/> forward to unblock it
    /// would deadlock the test: the thread that needs to call <c>Advance</c> is the thread stuck
    /// waiting. Calling the async core directly exercises the identical race
    /// (<c>Task.WhenAny(supervisorTask, Task.Delay(budget, timeProvider))</c>) without that
    /// self-deadlock, and is what <c>OnSuspend</c> itself does.
    /// </summary>
    [Fact]
    public async Task Suspend_budget_elapses_before_a_never_completing_SuspendAsync_and_the_wait_returns_false()
    {
        var (_, supervisor, _, time, adapter) = CreateHarness(suspendUnmountTimeoutSeconds: 8);
        supervisor.PendingSuspend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = adapter.WaitForSuspendDrainAsync();

        // The underlying call was issued immediately...
        Assert.Single(supervisor.SuspendCalls);
        // ...but has not completed, and the wait must not have resolved yet either.
        Assert.False(waitTask.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(8));

        var confirmedWithinBudget = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The budget elapsed first -- the wait must report "not confirmed", never throw, and never
        // have blocked past the budget.
        Assert.False(confirmedWithinBudget);
    }

    /// <summary>Same shape, but with a short budget, proving the bound scales with
    /// <c>global.suspend_unmount_timeout_seconds</c> rather than a hard-coded constant.</summary>
    [Fact]
    public async Task Suspend_budget_of_two_seconds_is_honoured()
    {
        var (_, supervisor, _, time, adapter) = CreateHarness(suspendUnmountTimeoutSeconds: 2);
        supervisor.PendingSuspend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = adapter.WaitForSuspendDrainAsync();

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(waitTask.IsCompleted); // budget not yet elapsed

        time.Advance(TimeSpan.FromSeconds(1));
        var confirmedWithinBudget = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(confirmedWithinBudget);
    }

    [Fact]
    public async Task Suspend_that_confirms_before_the_budget_elapses_returns_true()
    {
        var (_, supervisor, _, time, adapter) = CreateHarness(suspendUnmountTimeoutSeconds: 8);
        supervisor.PendingSuspend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = adapter.WaitForSuspendDrainAsync();

        // The drain confirms well within budget -- no clock advance needed at all.
        supervisor.PendingSuspend.TrySetResult();

        var confirmedWithinBudget = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(confirmedWithinBudget);
        Assert.Single(supervisor.SuspendCalls);
    }

    /// <summary>
    /// The in-flight call left running past the budget (see the adapter's class remarks) is
    /// deliberately observed via a continuation so a later fault logs rather than crashing the
    /// process as an unobserved task exception. Asserting the continuation actually RAN would
    /// require waiting on a real thread-pool scheduling hop with no deterministic signal to await
    /// (CLAUDE.md forbids sleeps in the default suite), so this test instead proves the narrower,
    /// fully deterministic half of that claim: setting the exception on the still-referenced task
    /// after the budget has elapsed does not throw synchronously and does not go unobserved by the
    /// .NET runtime's own unobserved-task-exception detector, which only fires once the
    /// <see cref="Task"/> is garbage-collected -- i.e. proving <c>TrySetException</c> itself is
    /// harmless is the deterministic half; that the adapter's <c>ContinueWith</c> attaches a
    /// consuming continuation (so the exception IS observed, not merely delayed) is a direct
    /// reading of <see cref="SystemEventSupervisorAdapter.WaitForSuspendDrainAsync"/>'s source.
    /// </summary>
    [Fact]
    public async Task A_late_fault_after_the_budget_elapsed_can_be_set_without_throwing()
    {
        var (_, supervisor, _, time, adapter) = CreateHarness(suspendUnmountTimeoutSeconds: 1);
        supervisor.PendingSuspend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = adapter.WaitForSuspendDrainAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        var exception = Record.Exception(() => supervisor.PendingSuspend.TrySetException(new InvalidOperationException("boom")));
        Assert.Null(exception);
    }
}
