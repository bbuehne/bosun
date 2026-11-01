using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace Bosun.SystemEventIntegration;

/// <summary>
/// Seam around the raw <c>+=</c>/<c>-=</c> calls to the three static event sources
/// <see cref="Win32SystemEventSource"/> wraps. Exists purely so the "subscribe once on
/// <see cref="ISystemEventSource.Start"/>, unsubscribe exactly once on <see cref="IDisposable.Dispose"/>"
/// discipline -- and the filtering/coalescing logic layered on top of it -- can be unit tested with
/// a spy in the default suite, without any test touching the real static
/// <c>Microsoft.Win32.SystemEvents</c>/<c>NetworkChange</c> classes (which fire on the OS's own
/// schedule; see <see cref="ISystemEventSource"/>'s remarks).
/// </summary>
/// <remarks>
/// This is an internal implementation detail of <see cref="Win32SystemEventSource"/>, not a second
/// public seam alongside <see cref="ISystemEventSource"/> -- the brief for bs-0wz asks for "one
/// interface, one Win32 implementation, one fake", and that is still exactly what callers of this
/// namespace see. <see cref="RealSystemEventRegistrar"/> is deliberately as thin as possible (pure
/// passthrough, no logic) so it needs no unit test of its own beyond compiling; everything with
/// actual behaviour (filtering <c>PowerModes.StatusChange</c>, filtering non-lock
/// <c>SessionSwitchReason</c> values, debouncing network-change bursts, and the
/// subscribe/unsubscribe bookkeeping itself) lives in <see cref="Win32SystemEventSource"/> and is
/// exercised through this seam instead.
/// </remarks>
internal interface ISystemEventRegistrar
{
    void AddPowerModeChanged(PowerModeChangedEventHandler handler);
    void RemovePowerModeChanged(PowerModeChangedEventHandler handler);
    void AddSessionSwitch(SessionSwitchEventHandler handler);
    void RemoveSessionSwitch(SessionSwitchEventHandler handler);

    /// <summary>
    /// <c>SystemEvents.SessionEnding</c> (logoff / shutdown in progress). Added for ADR-021
    /// Decision 5, whose Claude Code session classifier freezes while Windows ends the session.
    /// Nothing subscribes yet (bs-vqh.7 adds only the seam).
    /// </summary>
    void AddSessionEnding(SessionEndingEventHandler handler);
    void RemoveSessionEnding(SessionEndingEventHandler handler);
    void AddNetworkAddressChanged(NetworkAddressChangedEventHandler handler);
    void RemoveNetworkAddressChanged(NetworkAddressChangedEventHandler handler);
}

/// <summary>Production <see cref="ISystemEventRegistrar"/>: a pure passthrough to the real static
/// event sources. Constructing this does nothing observable by itself -- no subscription happens
/// until one of the <c>Add*</c> methods is actually called, matching every other worktree-safety
/// rule in this codebase (CLAUDE.md).</summary>
internal sealed class RealSystemEventRegistrar : ISystemEventRegistrar
{
    public void AddPowerModeChanged(PowerModeChangedEventHandler handler) => SystemEvents.PowerModeChanged += handler;

    public void RemovePowerModeChanged(PowerModeChangedEventHandler handler) => SystemEvents.PowerModeChanged -= handler;

    public void AddSessionSwitch(SessionSwitchEventHandler handler) => SystemEvents.SessionSwitch += handler;

    public void RemoveSessionSwitch(SessionSwitchEventHandler handler) => SystemEvents.SessionSwitch -= handler;

    public void AddSessionEnding(SessionEndingEventHandler handler) => SystemEvents.SessionEnding += handler;

    public void RemoveSessionEnding(SessionEndingEventHandler handler) => SystemEvents.SessionEnding -= handler;

    public void AddNetworkAddressChanged(NetworkAddressChangedEventHandler handler) => NetworkChange.NetworkAddressChanged += handler;

    public void RemoveNetworkAddressChanged(NetworkAddressChangedEventHandler handler) => NetworkChange.NetworkAddressChanged -= handler;
}
