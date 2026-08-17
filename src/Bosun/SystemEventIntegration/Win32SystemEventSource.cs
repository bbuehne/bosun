using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Bosun.SystemEventIntegration;

/// <summary>
/// Real <see cref="ISystemEventSource"/> (bs-0wz): wraps <c>Microsoft.Win32.SystemEvents</c> and
/// <c>System.Net.NetworkInformation.NetworkChange</c> behind the seam docs/ARCHITECTURE.md §3
/// describes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why network-change coalescing lives here, not in the adapter (E6b).</b>
/// <c>NetworkChange.NetworkAddressChanged</c> fires several times for one physical transition --
/// adapter down, DHCP release/renew, adapter up -- because Windows raises one notification per
/// intermediate link-layer state, not one per "the network changed" from a user's point of view.
/// Docking a laptop can produce a burst of four or five raw events within well under a second. That
/// burst is a property of <c>NetworkChange</c> itself, not of anything the consumer of this
/// interface does with the result -- every current and future subscriber of
/// <see cref="ISystemEventSource.NetworkAddressChanged"/> would otherwise have to reimplement the
/// same debounce independently. So the debounce is applied once, here, and everything downstream
/// (currently just <see cref="SystemEventSupervisorAdapter"/>) sees exactly one event per settled
/// transition and can treat it as a single trigger without its own timing logic. The debounce uses
/// the injected <see cref="TimeProvider"/> (trailing-edge: each raw event restarts the window; the
/// public event fires once the window elapses with no further raw events), so it is fully
/// deterministic and unit-tested with a fake clock -- see
/// <c>Bosun.Tests.SystemEventIntegration.Win32SystemEventSourceTests</c>.
/// </para>
/// <para>
/// <b>Why <see cref="ISystemEventRegistrar"/> instead of subscribing to the static classes
/// directly.</b> See that interface's remarks: it is what lets the filtering logic below
/// (<c>PowerModes.StatusChange</c> is not Suspend/Resume and must be ignored; only
/// <c>SessionSwitchReason.SessionLock</c> is acted on) and the subscribe-once/unsubscribe-on-dispose
/// discipline be proven deterministically in the default test suite via a spy, without touching the
/// real static <c>SystemEvents</c>/<c>NetworkChange</c> classes at all.
/// </para>
/// <para>
/// <b>Leak prevention.</b> <c>SystemEvents</c> holds every subscriber via a <i>static</i> event.
/// Constructing (and discarding) a <see cref="Win32SystemEventSource"/> without calling
/// <see cref="Dispose"/> would keep this instance -- and everything it transitively references --
/// alive for the remaining lifetime of the process, even though nothing else holds a reference to
/// it. <see cref="Dispose"/> removes every handler this instance added, exactly once, and is safe to
/// call multiple times or before <see cref="Start"/> was ever called.
/// </para>
/// </remarks>
public sealed class Win32SystemEventSource : ISystemEventSource
{
    /// <summary>How long a settled network transition must go quiet before the public event fires.
    /// No config field exists for this (docs/CONFIG-SCHEMA.md has nothing network-debounce shaped);
    /// a fixed internal constant, same treatment as <c>MountSupervisor</c>'s
    /// <c>DrainRetryInterval</c>/<c>DrainConfirmTimeout</c> -- flagged as discovered work in the
    /// delivery report rather than silently treated as tunable-via-config.</summary>
    private static readonly TimeSpan DefaultNetworkChangeDebounce = TimeSpan.FromMilliseconds(750);

    private readonly ISystemEventRegistrar registrar;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan networkChangeDebounce;
    private readonly ILogger<Win32SystemEventSource> logger;
    private readonly object gate = new();

    private readonly PowerModeChangedEventHandler powerModeHandler;
    private readonly SessionSwitchEventHandler sessionSwitchHandler;
    private readonly NetworkAddressChangedEventHandler networkAddressHandler;

    private ITimer? networkChangeDebounceTimer;
    private bool started;
    private bool disposed;

    public event EventHandler? Suspend;
    public event EventHandler? Resume;
    public event EventHandler? NetworkAddressChanged;
    public event EventHandler? SessionLock;

    public Win32SystemEventSource(TimeProvider timeProvider, ILogger<Win32SystemEventSource> logger)
        : this(new RealSystemEventRegistrar(), timeProvider, DefaultNetworkChangeDebounce, logger)
    {
    }

    /// <summary>Test/internal-only constructor: injects the registrar seam and lets the debounce
    /// window be shortened so tests never need to advance a fake clock by 750ms of simulated time
    /// just to prove the coalescing behaviour -- any positive window exercises the same code path.</summary>
    internal Win32SystemEventSource(
        ISystemEventRegistrar registrar, TimeProvider timeProvider, TimeSpan networkChangeDebounce, ILogger<Win32SystemEventSource> logger)
    {
        this.registrar = registrar;
        this.timeProvider = timeProvider;
        this.networkChangeDebounce = networkChangeDebounce;
        this.logger = logger;

        // Captured once so the exact same delegate instance is passed to both Add* (Start) and
        // Remove* (Dispose) -- SystemEvents' add/remove is delegate-equality based, same as any
        // .NET event; passing a fresh lambda to Remove* would silently fail to unsubscribe anything.
        powerModeHandler = OnPowerModeChanged;
        sessionSwitchHandler = OnSessionSwitch;
        networkAddressHandler = OnRawNetworkAddressChanged;
    }

    public void Start()
    {
        lock (gate)
        {
            if (started || disposed)
            {
                return;
            }

            started = true;

            registrar.AddPowerModeChanged(powerModeHandler);
            registrar.AddSessionSwitch(sessionSwitchHandler);
            registrar.AddNetworkAddressChanged(networkAddressHandler);

            logger.LogInformation("Subscribed to system power/network/session events");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            if (started)
            {
                registrar.RemovePowerModeChanged(powerModeHandler);
                registrar.RemoveSessionSwitch(sessionSwitchHandler);
                registrar.RemoveNetworkAddressChanged(networkAddressHandler);

                logger.LogInformation("Unsubscribed from system power/network/session events");
            }

            networkChangeDebounceTimer?.Dispose();
            networkChangeDebounceTimer = null;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Raw handlers. These run on SystemEvents' own dedicated thread (PowerModeChanged,
    // SessionSwitch) or a thread-pool thread (NetworkAddressChanged) -- never the UI thread, and
    // every one of them must return fast: SystemEvents processes handlers for ALL subscribers in
    // the process on that one dedicated thread, so a slow handler here would stall every other
    // subscriber, not just this class's own downstream consumer. Every method below does nothing
    // but filter and re-raise (or, for network changes, arm a timer) -- no I/O, no awaiting.
    // ------------------------------------------------------------------------------------------

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // PowerModeChanged also raises PowerModes.StatusChange (e.g. battery percentage, AC/DC
        // transitions) far more often than genuine suspend/resume -- only those two are meaningful
        // to this seam's contract.
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                logger.LogInformation("System event: power suspend");
                Suspend?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                logger.LogInformation("System event: power resume");
                Resume?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            logger.LogInformation("System event: session lock (v1: reserved, no action)");
            SessionLock?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRawNetworkAddressChanged(object? sender, EventArgs e)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            // Trailing-edge debounce: every raw event restarts the window. The public event fires
            // once no further raw event arrives for networkChangeDebounce -- see the class remarks
            // for why this lives here rather than in the adapter.
            networkChangeDebounceTimer?.Dispose();
            networkChangeDebounceTimer = timeProvider.CreateTimer(
                _ => FireNetworkAddressChanged(), null, networkChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireNetworkAddressChanged()
    {
        logger.LogInformation("System event: network address changed (settled after debounce)");
        NetworkAddressChanged?.Invoke(this, EventArgs.Empty);
    }
}
