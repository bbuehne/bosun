using Bosun.SystemEventIntegration;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Bosun.Tests.SystemEventIntegration.Independent;

/// <summary>
/// <c>Win32SystemEventSource</c>'s network-change coalescing and event filtering, attacked at the
/// boundaries rather than in the middle. The contract under test is
/// <c>ISystemEventSource.NetworkAddressChanged</c>'s own words: "Exactly one event per settled
/// network transition".
/// </summary>
/// <remarks>
/// <para>
/// Why this matters more than the laptop-docking story the code comments tell: on a desktop the
/// event that fires is a <b>network interruption</b> -- the switch reboots, the ISP blips, the NIC
/// renegotiates -- and each of those raises a burst of raw notifications too. Downstream, one
/// coalesced event costs a forced probe of every idle host <i>and</i> every mounted host
/// (<c>MountSupervisor.NetworkChangedAsync</c>), so over-firing during a flap is a probe storm
/// aimed at a link that is already struggling; under-firing means the drives do not come back.
/// </para>
/// <para>
/// Everything here runs through the <c>FakeSystemEventRegistrar</c> spy and a fake clock. No test
/// touches the real static <c>SystemEvents</c>/<c>NetworkChange</c> classes.
/// </para>
/// </remarks>
public sealed class NetworkCoalescingAdversarialTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(750);

    private static Win32SystemEventSource CreateSource(FakeSystemEventRegistrar registrar, FakeTimeProvider time) =>
        new(registrar, time, Window, NullLogger<Win32SystemEventSource>.Instance);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>
    /// <b>Protects:</b> "exactly one event per settled transition" at the last instant of the
    /// window.
    /// <b>Catches:</b> a debounce that arms the timer once and never genuinely restarts it -- e.g.
    /// re-arming with the ORIGINAL due time, or an <c>ITimer.Change</c> that is computed from the
    /// first raw event rather than the latest. The existing burst test spaces its raw events 200ms
    /// apart, comfortably inside the window, so it passes against that bug. Here the third-to-last
    /// raw event lands one tick before the deadline: if the restart is real, nothing fires at the
    /// original deadline at all.
    /// </summary>
    [Fact]
    public void A_raw_event_one_tick_before_the_deadline_restarts_the_whole_window()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time);
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        registrar.RaiseNetworkAddressChanged();
        time.Advance(Ms(749));
        registrar.RaiseNetworkAddressChanged();

        time.Advance(Ms(1)); // the ORIGINAL deadline
        Assert.Equal(0, fireCount);

        time.Advance(Ms(748));
        Assert.Equal(0, fireCount);

        time.Advance(Ms(1)); // the RESTARTED deadline
        Assert.Equal(1, fireCount);
    }

    /// <summary>
    /// <b>Protects:</b> the second half of the same sentence -- a genuinely new transition must not
    /// be swallowed by the one that just settled.
    /// <b>Catches:</b> a one-shot debounce that never re-arms after firing (a disposed timer field
    /// left non-null, or a "hasFired" latch), in the tightest possible interleaving: the next raw
    /// event arrives at the exact instant the previous one fires. On a desktop that is a switch
    /// coming back up moments after it went down -- and if the second transition is dropped, the
    /// mounted hosts are never force-probed, so a drive pointing at a host that moved is left
    /// wedged until the ordinary probe cadence catches up.
    /// </summary>
    [Fact]
    public void A_new_transition_arriving_at_the_instant_the_previous_one_fires_is_not_swallowed()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time);
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        registrar.RaiseNetworkAddressChanged();
        time.Advance(Window);
        Assert.Equal(1, fireCount);

        registrar.RaiseNetworkAddressChanged(); // same instant on the fake clock as the fire above
        time.Advance(Window);

        Assert.Equal(2, fireCount);
    }

    /// <summary>
    /// <b>Protects:</b> "exactly one event per settled transition" across a link that keeps flapping
    /// for longer than the debounce window -- ten raw notifications spread over seven seconds, which
    /// is what a rebooting switch or a renegotiating NIC actually looks like.
    /// <b>Catches:</b> a fixed-window throttle masquerading as a trailing-edge debounce. That
    /// variant fires roughly once per window for the whole flap -- nine
    /// <c>NetworkChangedAsync</c> calls here, each of which force-probes every idle host and every
    /// mounted host. A probe storm aimed at a link that is already down is the opposite of what the
    /// coalescing exists for, and it is invisible to a burst test whose events all fit inside one
    /// window.
    /// </summary>
    /// <remarks>
    /// This also pins a real design consequence: because the debounce is purely trailing-edge with no
    /// maximum deferral, a link that flaps faster than the window defers the public event
    /// indefinitely. Flagged in the delivery report as a spec gap rather than silently accepted --
    /// docs/ARCHITECTURE.md and docs/CONFIG-SCHEMA.md say nothing about the debounce at all.
    /// </remarks>
    [Fact]
    public void A_flap_lasting_longer_than_the_window_still_produces_exactly_one_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time);
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        for (var i = 0; i < 10; i++)
        {
            registrar.RaiseNetworkAddressChanged();
            time.Advance(Ms(700)); // just inside the 750ms window, ten times over
        }

        Assert.Equal(0, fireCount);

        time.Advance(Window); // the link finally settles
        Assert.Equal(1, fireCount);
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I8's timeliness. §3 gives the suspend handler a bounded window
    /// because "Windows does not wait indefinitely"; a coalesced suspend would spend part of that
    /// window not having been delivered yet.
    /// <b>Catches:</b> the debounce being applied to the wrong events -- i.e. a refactor that routes
    /// <c>Suspend</c>/<c>Resume</c> through the same timer as the network path "for consistency". A
    /// suspend delayed by 750ms on a machine that is already going to sleep is a suspend that never
    /// arrives, and the drives sleep mounted. Asserted by advancing the clock <b>not at all</b>.
    /// </summary>
    [Theory]
    [InlineData(PowerModes.Suspend)]
    [InlineData(PowerModes.Resume)]
    public void Power_events_are_delivered_synchronously_and_are_never_debounced(PowerModes mode)
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time);
        source.Start();
        var count = 0;
        source.Suspend += (_, _) => count++;
        source.Resume += (_, _) => count++;

        registrar.RaisePowerModeChanged(mode);

        Assert.Equal(1, count);
    }

    /// <summary>
    /// <b>Protects:</b> the leak/lifetime discipline in <c>Win32SystemEventSource</c>'s remarks,
    /// extended to the piece <c>Dispose</c> could plausibly forget: the debounce timer it may have
    /// left armed.
    /// <b>Catches:</b> a <c>Dispose</c> that detaches the registrar handlers but leaves a pending
    /// timer running. Its callback holds this instance (and transitively the adapter and the
    /// supervisor) alive, and when it fires it raises <c>NetworkAddressChanged</c> on a component
    /// the host has already shut down -- a <c>NetworkChangedAsync</c> issued against a supervisor
    /// whose command channel is completed. In a test run the same leak survives into every later
    /// test in the process, which is exactly the class of failure that makes an unrelated suite go
    /// flaky weeks later.
    /// </summary>
    [Fact]
    public void Dispose_mid_window_cancels_the_pending_network_event()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();
        var source = CreateSource(registrar, time);
        source.Start();
        var fireCount = 0;
        source.NetworkAddressChanged += (_, _) => fireCount++;

        registrar.RaiseNetworkAddressChanged();
        time.Advance(Ms(700)); // inside the window: the timer is armed and has not fired

        source.Dispose();
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, fireCount);
    }
}
