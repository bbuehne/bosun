using Bosun.SystemEventIntegration;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Bosun.Tests.SystemEventIntegration;

/// <summary>
/// <see cref="Win32SystemEventSource"/> (bs-0wz), exercised entirely through the
/// <see cref="FakeSystemEventRegistrar"/> seam -- no test here touches the real static
/// <c>SystemEvents</c>/<c>NetworkChange</c> classes, which fire on the OS's own schedule, not the
/// test's (see <see cref="ISystemEventSource"/>'s remarks). Real-event coverage is
/// <see cref="Win32SystemEventSourceIntegrationTests"/>, marked
/// <c>[Trait(TestCategories.Category, TestCategories.Integration)]</c>.
/// </summary>
public sealed class Win32SystemEventSourceTests
{
    private static Win32SystemEventSource CreateSource(
        FakeSystemEventRegistrar registrar, FakeTimeProvider time, TimeSpan? debounce = null) =>
        new(registrar, time, debounce ?? TimeSpan.FromMilliseconds(750), NullLogger<Win32SystemEventSource>.Instance);

    // --------------------------------------------------------------------------------------
    // Subscribe-once / unsubscribe-on-dispose discipline (SystemEvents holds handlers via a
    // STATIC event -- failing to unsubscribe leaks the subscriber for the process lifetime).
    // --------------------------------------------------------------------------------------

    [Fact]
    public void Start_subscribes_to_all_three_raw_sources_exactly_once()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());

        source.Start();

        Assert.Equal(1, registrar.AddPowerModeChangedCalls);
        Assert.Equal(1, registrar.AddSessionSwitchCalls);
        Assert.Equal(1, registrar.AddNetworkAddressChangedCalls);
        Assert.True(registrar.IsPowerModeSubscribed);
        Assert.True(registrar.IsSessionSwitchSubscribed);
        Assert.True(registrar.IsNetworkAddressSubscribed);
    }

    [Fact]
    public void Start_called_twice_subscribes_only_once()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());

        source.Start();
        source.Start();

        Assert.Equal(1, registrar.AddPowerModeChangedCalls);
        Assert.Equal(1, registrar.AddSessionSwitchCalls);
        Assert.Equal(1, registrar.AddNetworkAddressChangedCalls);
    }

    [Fact]
    public void Dispose_unsubscribes_from_all_three_raw_sources_exactly_once()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();

        source.Dispose();

        Assert.Equal(1, registrar.RemovePowerModeChangedCalls);
        Assert.Equal(1, registrar.RemoveSessionSwitchCalls);
        Assert.Equal(1, registrar.RemoveNetworkAddressChangedCalls);
        Assert.False(registrar.IsPowerModeSubscribed);
        Assert.False(registrar.IsSessionSwitchSubscribed);
        Assert.False(registrar.IsNetworkAddressSubscribed);
    }

    [Fact]
    public void Dispose_called_twice_unsubscribes_only_once()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, registrar.RemovePowerModeChangedCalls);
        Assert.Equal(1, registrar.RemoveSessionSwitchCalls);
        Assert.Equal(1, registrar.RemoveNetworkAddressChangedCalls);
    }

    [Fact]
    public void Dispose_without_Start_does_not_unsubscribe_anything()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());

        source.Dispose();

        Assert.Equal(0, registrar.RemovePowerModeChangedCalls);
        Assert.Equal(0, registrar.RemoveSessionSwitchCalls);
        Assert.Equal(0, registrar.RemoveNetworkAddressChangedCalls);
    }

    [Fact]
    public void After_Dispose_a_raw_event_no_longer_reaches_the_public_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();
        var suspendCount = 0;
        source.Suspend += (_, _) => suspendCount++;

        source.Dispose();

        // FakeSystemEventRegistrar's own Raise* methods are no-ops once the handler has been
        // removed (the handler field goes null on Remove*), which is exactly what real Dispose
        // must achieve against the real static SystemEvents class.
        registrar.RaisePowerModeChanged(PowerModes.Suspend);
        Assert.Equal(0, suspendCount);
    }

    // --------------------------------------------------------------------------------------
    // PowerModeChanged filtering: only Suspend/Resume are meaningful. StatusChange (battery,
    // AC/DC) fires far more often and must raise neither public event.
    // --------------------------------------------------------------------------------------

    [Fact]
    public void PowerModeChanged_Suspend_raises_the_public_Suspend_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();
        var suspendCount = 0;
        var resumeCount = 0;
        source.Suspend += (_, _) => suspendCount++;
        source.Resume += (_, _) => resumeCount++;

        registrar.RaisePowerModeChanged(PowerModes.Suspend);

        Assert.Equal(1, suspendCount);
        Assert.Equal(0, resumeCount);
    }

    [Fact]
    public void PowerModeChanged_Resume_raises_the_public_Resume_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();
        var suspendCount = 0;
        var resumeCount = 0;
        source.Suspend += (_, _) => suspendCount++;
        source.Resume += (_, _) => resumeCount++;

        registrar.RaisePowerModeChanged(PowerModes.Resume);

        Assert.Equal(0, suspendCount);
        Assert.Equal(1, resumeCount);
    }

    [Fact]
    public void PowerModeChanged_StatusChange_raises_neither_public_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();
        var suspendCount = 0;
        var resumeCount = 0;
        source.Suspend += (_, _) => suspendCount++;
        source.Resume += (_, _) => resumeCount++;

        registrar.RaisePowerModeChanged(PowerModes.StatusChange);

        Assert.Equal(0, suspendCount);
        Assert.Equal(0, resumeCount);
    }

    // --------------------------------------------------------------------------------------
    // SessionSwitch filtering: only SessionLock is acted on (v1: reserved).
    // --------------------------------------------------------------------------------------

    [Fact]
    public void SessionSwitch_SessionLock_raises_the_public_SessionLock_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();
        var lockCount = 0;
        source.SessionLock += (_, _) => lockCount++;

        registrar.RaiseSessionSwitch(SessionSwitchReason.SessionLock);

        Assert.Equal(1, lockCount);
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock)]
    [InlineData(SessionSwitchReason.SessionLogon)]
    [InlineData(SessionSwitchReason.SessionLogoff)]
    [InlineData(SessionSwitchReason.ConsoleConnect)]
    public void SessionSwitch_other_reasons_raise_nothing(SessionSwitchReason reason)
    {
        var registrar = new FakeSystemEventRegistrar();
        var source = CreateSource(registrar, new FakeTimeProvider());
        source.Start();
        var lockCount = 0;
        source.SessionLock += (_, _) => lockCount++;

        registrar.RaiseSessionSwitch(reason);

        Assert.Equal(0, lockCount);
    }

    // --------------------------------------------------------------------------------------
    // Network-change coalescing: see Win32SystemEventSource's class remarks for why this lives
    // here (E6a) rather than in the adapter (E6b).
    // --------------------------------------------------------------------------------------

    [Fact]
    public void NetworkAddressChanged_a_single_raw_event_fires_once_the_debounce_window_elapses()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time, TimeSpan.FromMilliseconds(750));
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        registrar.RaiseNetworkAddressChanged();
        Assert.Equal(0, fireCount); // not yet -- still inside the debounce window

        time.Advance(TimeSpan.FromMilliseconds(749));
        Assert.Equal(0, fireCount); // still not settled

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fireCount); // settled
    }

    [Fact]
    public void NetworkAddressChanged_a_burst_of_raw_events_coalesces_into_exactly_one_public_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time, TimeSpan.FromMilliseconds(750));
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        // Simulates docking a laptop: adapter down, DHCP, adapter up -- three raw notifications
        // for one physical transition, each well inside the debounce window of the previous one.
        registrar.RaiseNetworkAddressChanged();
        time.Advance(TimeSpan.FromMilliseconds(200));
        registrar.RaiseNetworkAddressChanged();
        time.Advance(TimeSpan.FromMilliseconds(200));
        registrar.RaiseNetworkAddressChanged();

        Assert.Equal(0, fireCount); // still coalescing

        time.Advance(TimeSpan.FromMilliseconds(750));

        Assert.Equal(1, fireCount); // exactly one event for the whole burst
    }

    [Fact]
    public void NetworkAddressChanged_fires_again_for_a_separate_settled_transition()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time, TimeSpan.FromMilliseconds(750));
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        registrar.RaiseNetworkAddressChanged();
        time.Advance(TimeSpan.FromMilliseconds(750));
        Assert.Equal(1, fireCount);

        // A second, later, independent transition after the first has fully settled.
        registrar.RaiseNetworkAddressChanged();
        time.Advance(TimeSpan.FromMilliseconds(750));
        Assert.Equal(2, fireCount);
    }
}
