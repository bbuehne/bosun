using Bosun.SystemEventIntegration;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Bosun.Tests.SystemEventIntegration.Independent.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.SystemEventIntegration.Independent;

/// <summary>
/// The adapter's wiring contract from docs/ARCHITECTURE.md §3's <c>ISystemEventSource</c> table --
/// one row per event, and one row (<c>SessionLock</c>) whose specified reaction is "no action (v1);
/// reserved" -- plus the subscription lifetime that table implies.
/// </summary>
/// <remarks>
/// Nothing here subscribes to a real system event, reaches a real mount, drive letter, rclone, or
/// WinFsp. The event source is a fake and the supervisor is a recording spy.
/// </remarks>
public sealed class SystemEventWiringAdversarialTests
{
    private static (FakeSystemEventSource EventSource, SupervisorSpy Supervisor, SystemEventSupervisorAdapter Adapter)
        CreateHarness()
    {
        var eventSource = new FakeSystemEventSource();
        var supervisor = new SupervisorSpy();
        var configStore = new MutableConfigStore(HostFixtures.Build(HostFixtures.Global()));
        var adapter = new SystemEventSupervisorAdapter(
            eventSource, supervisor, configStore, new ObservableFakeClock(),
            NullLogger<SystemEventSupervisorAdapter>.Instance);

        return (eventSource, supervisor, adapter);
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §3 -- <c>SessionSwitch → SessionLock</c>: "No action
    /// (v1); reserved".
    /// <b>Catches:</b> any supervisor call at all being wired to the lock event. Asserted as a
    /// literal call count of zero across the WHOLE interface, not as three specific lists being
    /// empty and not as an absence of a thrown exception: the spy records every member and throws
    /// from none, so wiring the lock to (say) <c>SuspendAsync</c>, <c>RequestUnmountAsync</c>, or a
    /// speculative <c>GetSnapshot</c> refresh is caught by the same assertion. Locking a workstation
    /// is a many-times-a-day event; a mount action attached to it would unmount the maintainer's
    /// drives every time he walked away from the desk.
    /// </summary>
    [Fact]
    public void SessionLock_calls_nothing_on_the_supervisor_however_many_times_it_fires()
    {
        var (eventSource, supervisor, _) = CreateHarness();

        eventSource.RaiseSessionLock();
        eventSource.RaiseSessionLock();
        eventSource.RaiseSessionLock();

        Assert.Empty(supervisor.Calls);
    }

    /// <summary>
    /// <b>Protects:</b> the <c>ISystemEventSource</c> table's one-row-per-event mapping -- no event
    /// may fan out into a reaction belonging to a different row.
    /// <b>Catches:</b> a mis-wired handler (resume calling <c>NetworkChangedAsync</c> "because it
    /// also re-probes", a network change opportunistically calling <c>ResumeAsync</c>). Both would
    /// pass a test that only asserts "the expected call happened", and both change real behaviour:
    /// <c>ResumeAsync</c> clears the supervisor's <c>suspended</c> latch and re-enables hosts parked
    /// by a suspend, which a network change must never do while the machine is going to sleep.
    /// </summary>
    [Fact]
    public void Each_event_produces_exactly_its_own_supervisor_call_and_no_other()
    {
        var (eventSource, supervisor, _) = CreateHarness();

        eventSource.RaiseNetworkAddressChanged();
        eventSource.RaiseResume();
        eventSource.RaiseSuspend();

        Assert.Equal(["NetworkChangedAsync", "ResumeAsync", "SuspendAsync"], supervisor.Calls);
    }

    /// <summary>
    /// <b>Protects:</b> the leak note in <c>Win32SystemEventSource</c>'s remarks, one level up.
    /// <c>Microsoft.Win32.SystemEvents</c> holds subscribers through a <b>static</b> event, so the
    /// chain source → adapter → supervisor stays reachable for the whole process lifetime if any
    /// link fails to detach.
    /// <b>Catches:</b> an adapter <c>Dispose</c> that unsubscribes some handlers but not others, or
    /// that detaches a different delegate instance than it attached (the classic
    /// <c>x.Event -= OnX</c>-with-a-lambda mistake, which compiles and silently detaches nothing).
    /// Asserted behaviourally -- after disposal no event may reach the supervisor -- because that is
    /// the property that matters; counting handler fields would assert the implementation instead.
    /// In a test run this is also a cross-test hazard: a leaked adapter from an earlier test keeps
    /// answering events for the rest of the process.
    /// </summary>
    [Fact]
    public void After_Dispose_no_system_event_reaches_the_supervisor()
    {
        var (eventSource, supervisor, adapter) = CreateHarness();

        adapter.Dispose();

        eventSource.RaiseSuspend();
        eventSource.RaiseResume();
        eventSource.RaiseNetworkAddressChanged();
        eventSource.RaiseSessionLock();

        Assert.Empty(supervisor.Calls);
    }

    /// <summary>
    /// <b>Protects:</b> the same unsubscribe discipline against a double dispose (a
    /// <c>using</c> plus an explicit call, or DI disposing a component the host already disposed).
    /// <b>Catches:</b> a <c>Dispose</c> that is not idempotent -- most plausibly one that throws
    /// <c>ObjectDisposedException</c> on the second call, which during host shutdown surfaces as a
    /// noisy, misleading crash on exit rather than a clean stop.
    /// </summary>
    [Fact]
    public void Dispose_is_idempotent_and_leaves_the_supervisor_untouched()
    {
        var (eventSource, supervisor, adapter) = CreateHarness();

        adapter.Dispose();
        var second = Record.Exception(adapter.Dispose);

        Assert.Null(second);
        eventSource.RaiseSuspend();
        Assert.Empty(supervisor.Calls);
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §4 rule 5 -- resume is what moves previously-enabled
    /// hosts back to <c>Probing</c>.
    /// <b>Catches:</b> an adapter that only honours a resume it has seen a matching suspend for.
    /// Windows raises <c>PowerModeChanged(Resume)</c> in cases where no suspend notification ever
    /// reached this process -- a vetoed suspend, a modern-standby wake, and (the case that matters
    /// on the maintainer's desktop) an app started or restarted while the machine was already
    /// asleep. If the adapter needs a prior suspend to accept a resume, hosts never re-probe after
    /// the wake and the drives stay gone until a restart.
    /// </summary>
    [Fact]
    public void Resume_without_a_preceding_suspend_is_still_forwarded()
    {
        var (eventSource, supervisor, _) = CreateHarness();

        eventSource.RaiseResume();

        Assert.Equal(1, supervisor.ResumeCalls);
        Assert.Equal(0, supervisor.SuspendCalls);
    }
}
