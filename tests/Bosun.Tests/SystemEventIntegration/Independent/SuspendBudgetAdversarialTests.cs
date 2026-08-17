using Bosun.SystemEventIntegration;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Bosun.Tests.SystemEventIntegration.Independent.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.SystemEventIntegration.Independent;

/// <summary>
/// Invariant I8 and docs/ARCHITECTURE.md §3's suspend-handler paragraph ("the suspend handler must
/// complete quickly. Windows does not wait indefinitely. Issue unmounts with a short timeout and
/// accept that a forced unmount may be needed on resume"), attacked at the one place the delivery
/// is least obviously correct: <c>SystemEventSupervisorAdapter.OnSuspend</c>'s synchronous
/// <c>.GetAwaiter().GetResult()</c> wrapper.
/// </summary>
/// <remarks>
/// <para>
/// The scenario weighted heaviest here is deliberately not "an idle machine suspends". The
/// maintainer runs a desktop; his real failure mode is an overnight sleep that begins <b>while
/// something is going on</b> -- a probe in flight, another host mid-drain, the supervisor's single
/// command channel (docs/ARCHITECTURE.md §5) already busy. That is the only case in which the
/// budget does any work at all; a test against an idle supervisor proves the budget exists but
/// never exercises it.
/// </para>
/// <para>
/// Nothing here touches WinFsp, a real host, a real drive letter, or a real system event: a spy
/// supervisor, an in-memory config, a fake clock, and the fake event source.
/// </para>
/// </remarks>
public sealed class SuspendBudgetAdversarialTests
{
    private sealed record Harness(
        FakeSystemEventSource EventSource,
        SupervisorSpy Supervisor,
        MutableConfigStore ConfigStore,
        ObservableFakeClock Clock,
        SystemEventSupervisorAdapter Adapter);

    private static Harness CreateHarness(int suspendUnmountTimeoutSeconds = 8)
    {
        var eventSource = new FakeSystemEventSource();
        var supervisor = new SupervisorSpy();
        var configStore = new MutableConfigStore(
            HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: suspendUnmountTimeoutSeconds)));
        var clock = new ObservableFakeClock();
        var adapter = new SystemEventSupervisorAdapter(
            eventSource, supervisor, configStore, clock, NullLogger<SystemEventSupervisorAdapter>.Instance);

        return new Harness(eventSource, supervisor, configStore, clock, adapter);
    }

    /// <summary>Generous failure guard for the two-thread rendezvous below. No assertion depends on
    /// its value; it only converts a hang into a readable failure.</summary>
    private static readonly TimeSpan HandshakeGuard = TimeSpan.FromSeconds(30);

    // ==============================================================================================
    // The blocking wrapper itself, exercised through the real event handler.
    // ==============================================================================================

    /// <summary>
    /// <b>Protects:</b> Invariant I8 / docs/ARCHITECTURE.md §3 ("the suspend handler must complete
    /// quickly. Windows does not wait indefinitely").
    /// <b>Catches:</b> a suspend handler that never returns when the supervisor's command channel is
    /// busy -- i.e. the blocking <c>OnSuspend</c> wrapper failing to be bounded even though the async
    /// core it calls is. The existing suite proves the bound on
    /// <c>WaitForSuspendDrainAsync</c> only, and documents that it deliberately does not drive the
    /// public path; this test drives the public path, because in production nothing ever calls the
    /// internal method -- Windows calls the handler.
    /// </summary>
    /// <remarks>
    /// Deterministic despite the second thread: the raise happens on a worker, and the clock is only
    /// advanced after <see cref="ObservableFakeClock.TimerArmed"/> proves the budget timer created by
    /// <c>Task.Delay(budget, timeProvider)</c> already exists. There is no interleaving in which the
    /// advance can be lost. No sleeps and no wall-clock dependence.
    /// </remarks>
    [Fact]
    public async Task The_blocking_suspend_handler_returns_when_the_budget_elapses_with_the_channel_still_busy()
    {
        var h = CreateHarness(suspendUnmountTimeoutSeconds: 8);

        // A drain for some other host is already in flight, so SuspendAsync's own continuation
        // cannot be dequeued: the task it returned will not complete inside this test at all.
        var busyChannel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Supervisor.PendingSuspends.Enqueue(busyChannel);

        var handler = Task.Run(() => h.EventSource.RaiseSuspend());

        Assert.True(
            h.Clock.TimerArmed.Wait(HandshakeGuard),
            "The suspend handler never armed its budget timer -- it is not bounded by " +
            "global.suspend_unmount_timeout_seconds at all.");
        Assert.Equal(1, h.Supervisor.SuspendCalls);

        // Deterministic, not racy: past the signal above the handler can only be released by the
        // budget timer (advanced below) or by the drain completing (it never does in this test).
        Assert.False(
            handler.IsCompleted,
            "The suspend handler returned before its budget elapsed and before the drain confirmed -- " +
            "it is fire-and-forget. Windows proceeds with the suspend as soon as the handler returns " +
            "(docs/ARCHITECTURE.md §3), so a handler that does not block gives the drain no window at " +
            "all and Invariant I8 is unenforced.");

        h.Clock.Advance(TimeSpan.FromSeconds(8));

        try
        {
            await handler.WaitAsync(HandshakeGuard);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                "The suspend handler did not return once the Invariant I8 budget elapsed. In " +
                "production this is Microsoft.Win32.SystemEvents' single process-wide notification " +
                "thread, so it would stall every other subscriber in the process while Windows " +
                "suspends anyway.");
        }
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §3 -- "issue unmounts with a short timeout and accept
    /// that a forced unmount may be needed on resume", plus §6's "machine sleeps with mounts up ...
    /// if we missed it, force-unmount and reconcile on resume". Both sentences presuppose that the
    /// unmount we gave up <i>waiting</i> for is still an unmount in progress, not a cancelled one.
    /// <b>Catches:</b> the obvious "fix" to a slow drain -- bounding it with
    /// <c>new CancellationTokenSource(budget)</c> and passing that token to
    /// <c>SuspendAsync</c>. That cancels an in-flight <c>mount/unmount</c> and its
    /// <c>mount/listmounts</c> verification partway through, which is how the state machine ends up
    /// believing a drive is gone when it is not (§4 rule 4, Invariant I2) -- the worst possible
    /// outcome to leave behind at the moment the machine sleeps.
    /// </summary>
    [Fact]
    public async Task The_elapsed_budget_stops_waiting_for_the_drain_but_never_cancels_it()
    {
        var h = CreateHarness(suspendUnmountTimeoutSeconds: 4);
        var inFlightDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Supervisor.PendingSuspends.Enqueue(inFlightDrain);

        var wait = h.Adapter.WaitForSuspendDrainAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(4));

        Assert.False(await wait.WaitAsync(HandshakeGuard));

        var token = Assert.Single(h.Supervisor.SuspendTokens);
        Assert.False(
            token.IsCancellationRequested,
            "The adapter cancelled the drain when its budget elapsed. The budget bounds how long the " +
            "HANDLER blocks, not how long the unmount is allowed to take -- a cancelled unmount is a " +
            "half-removed drive letter, which is the Invariant I2 failure this project exists to stop.");

        // And the abandoned drain must still be able to finish on its own afterwards.
        Assert.True(inFlightDrain.TrySetResult());
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I8 -- suspend must not be able to take the process down, because a
    /// process that dies on suspend never runs the resume-side reconciliation §6 relies on to clean
    /// up whatever the suspend failed to unmount.
    /// <b>Catches:</b> a suspend-time supervisor fault escaping the event handler. Sibling paths
    /// (<c>OnResume</c>, <c>OnNetworkAddressChanged</c>) log their faults through
    /// <c>FireAndForget</c>; the suspend path re-throws instead, and <c>OnSuspend</c> turns that back
    /// into a synchronous throw with <c>.GetAwaiter().GetResult()</c>. In production the throw
    /// happens on <c>Microsoft.Win32.SystemEvents</c>' own dedicated notification thread, inside
    /// <c>Win32SystemEventSource.OnPowerModeChanged</c> -- there is no <c>catch</c> anywhere above it.
    /// </summary>
    /// <remarks>
    /// <c>MountSupervisor.SuspendAsync</c> can genuinely fault: <c>EnqueueAndWait</c> catches every
    /// exception from the queued action and re-surfaces it as <c>TrySetException</c> on the task it
    /// returns, so any unexpected throw inside the drain sweep (an invalid-transition guard, a
    /// collection-mutation race, a null host config) arrives here as a faulted task rather than as a
    /// log line. THIS TEST IS EXPECTED TO FAIL against the implementation as written -- see the
    /// delivery report; it is not weakened to match.
    /// </remarks>
    [Fact]
    public void A_faulting_SuspendAsync_must_not_escape_the_suspend_event_handler()
    {
        var h = CreateHarness(suspendUnmountTimeoutSeconds: 8);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        faulted.SetException(new InvalidOperationException("rclone rcd went away mid-drain"));
        h.Supervisor.PendingSuspends.Enqueue(faulted);

        var escaped = Record.Exception(() => h.EventSource.RaiseSuspend());

        Assert.Null(escaped);
    }

    // ==============================================================================================
    // Around the budget: the events that arrive next.
    // ==============================================================================================

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §4 rule 5 ("on resume, everything previously enabled
    /// goes to Probing") and docs/OPERATIONS.md T1.
    /// <b>Catches:</b> a guard that suppresses resume handling while a suspend issued earlier is
    /// still outstanding. This is the exact shape of the overnight sleep: the budget elapsed, the
    /// drain was left running in the background, the machine slept mid-drain, and now it is awake.
    /// If the resume is swallowed because "a suspend is still in flight", every drive stays gone
    /// until the app is restarted -- a silent, permanent regression that only shows up after a real
    /// sleep, never in an idle test.
    /// </summary>
    [Fact]
    public async Task Resume_is_still_delivered_while_the_abandoned_suspend_drain_is_outstanding()
    {
        var h = CreateHarness(suspendUnmountTimeoutSeconds: 2);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Supervisor.PendingSuspends.Enqueue(neverCompletes);

        var wait = h.Adapter.WaitForSuspendDrainAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await wait.WaitAsync(HandshakeGuard));

        h.EventSource.RaiseResume();

        Assert.Equal(1, h.Supervisor.ResumeCalls);
        Assert.Equal(["SuspendAsync", "ResumeAsync"], h.Supervisor.Calls);
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I8 on the second of two suspends. Windows raises
    /// <c>PowerModeChanged(Suspend)</c> again when a suspend is vetoed and re-attempted, and after a
    /// modern-standby wake that never produced a visible resume.
    /// <b>Catches:</b> a latch ("we already suspended, skip") that makes the second suspend a no-op.
    /// Whatever was remounted between the two attempts would then sleep with its drive letter up,
    /// which is the wedged-Explorer state on the next wake.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_after_the_first_ones_budget_elapsed_still_issues_its_own_drain()
    {
        var h = CreateHarness(suspendUnmountTimeoutSeconds: 3);
        var firstNeverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Supervisor.PendingSuspends.Enqueue(firstNeverCompletes);

        var wait = h.Adapter.WaitForSuspendDrainAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await wait.WaitAsync(HandshakeGuard));

        // Second suspend: nothing queued, so it resolves immediately and can go through the real
        // blocking handler without any clock movement.
        h.EventSource.RaiseSuspend();

        Assert.Equal(2, h.Supervisor.SuspendCalls);
    }

    /// <summary>
    /// <b>Protects:</b> ADR-012's principle that configuration is live, applied to Invariant I8's own
    /// budget -- <c>global.suspend_unmount_timeout_seconds</c> is read per event, not captured once.
    /// <b>Catches:</b> hoisting the budget into a constructor field. Bosun's config is watched and
    /// reloaded at runtime (<c>IHostConfigStore</c>), so a maintainer who shortens the budget after
    /// finding Windows cutting the handler off would see no change at all until the next restart --
    /// and would conclude the setting does nothing.
    /// </summary>
    [Fact]
    public async Task The_suspend_budget_is_read_from_the_live_config_on_every_suspend()
    {
        var h = CreateHarness(suspendUnmountTimeoutSeconds: 60);
        h.Supervisor.PendingSuspends.Enqueue(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        // The user reloads hosts.toml with a much shorter budget before the machine ever suspends.
        h.ConfigStore.Current = HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: 5));

        var wait = h.Adapter.WaitForSuspendDrainAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(wait.IsCompleted);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(
            await wait.WaitAsync(HandshakeGuard),
            "The reloaded 5s budget was not applied -- the adapter is still using the value it saw at " +
            "construction.");
    }
}
