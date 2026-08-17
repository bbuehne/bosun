using System.Net.NetworkInformation;
using Bosun.SystemEventIntegration;
using Microsoft.Win32;

namespace Bosun.Tests.SystemEventIntegration.Fakes;

/// <summary>
/// Spy <see cref="ISystemEventRegistrar"/>: records every Add/Remove call and lets a test raise a
/// "raw" event directly (i.e. before <see cref="Win32SystemEventSource"/>'s own filtering/debounce
/// logic runs) without ever touching the real static <c>SystemEvents</c>/<c>NetworkChange</c>
/// classes. This is what makes <c>Win32SystemEventSourceTests</c> -- including the network-change
/// debounce/coalescing behaviour and the subscribe-once/unsubscribe-on-dispose discipline -- fully
/// deterministic and safe for the default suite.
/// </summary>
internal sealed class FakeSystemEventRegistrar : ISystemEventRegistrar
{
    private PowerModeChangedEventHandler? powerModeHandler;
    private SessionSwitchEventHandler? sessionSwitchHandler;
    private NetworkAddressChangedEventHandler? networkAddressHandler;

    public int AddPowerModeChangedCalls { get; private set; }
    public int RemovePowerModeChangedCalls { get; private set; }
    public int AddSessionSwitchCalls { get; private set; }
    public int RemoveSessionSwitchCalls { get; private set; }
    public int AddNetworkAddressChangedCalls { get; private set; }
    public int RemoveNetworkAddressChangedCalls { get; private set; }

    public void AddPowerModeChanged(PowerModeChangedEventHandler handler)
    {
        AddPowerModeChangedCalls++;
        powerModeHandler = handler;
    }

    public void RemovePowerModeChanged(PowerModeChangedEventHandler handler)
    {
        RemovePowerModeChangedCalls++;
        if (ReferenceEquals(powerModeHandler, handler))
        {
            powerModeHandler = null;
        }
    }

    public void AddSessionSwitch(SessionSwitchEventHandler handler)
    {
        AddSessionSwitchCalls++;
        sessionSwitchHandler = handler;
    }

    public void RemoveSessionSwitch(SessionSwitchEventHandler handler)
    {
        RemoveSessionSwitchCalls++;
        if (ReferenceEquals(sessionSwitchHandler, handler))
        {
            sessionSwitchHandler = null;
        }
    }

    public void AddNetworkAddressChanged(NetworkAddressChangedEventHandler handler)
    {
        AddNetworkAddressChangedCalls++;
        networkAddressHandler = handler;
    }

    public void RemoveNetworkAddressChanged(NetworkAddressChangedEventHandler handler)
    {
        RemoveNetworkAddressChangedCalls++;
        if (ReferenceEquals(networkAddressHandler, handler))
        {
            networkAddressHandler = null;
        }
    }

    /// <summary>True once subscribed and not yet unsubscribed -- the direct, deterministic proxy
    /// this test suite uses in place of reflecting into the real static SystemEvents class's
    /// private handler bookkeeping (see the class remarks on <see cref="FakeSystemEventRegistrar"/>).</summary>
    public bool IsPowerModeSubscribed => powerModeHandler is not null;
    public bool IsSessionSwitchSubscribed => sessionSwitchHandler is not null;
    public bool IsNetworkAddressSubscribed => networkAddressHandler is not null;

    public void RaisePowerModeChanged(PowerModes mode) =>
        powerModeHandler?.Invoke(this, new PowerModeChangedEventArgs(mode));

    public void RaiseSessionSwitch(SessionSwitchReason reason) =>
        sessionSwitchHandler?.Invoke(this, new SessionSwitchEventArgs(reason));

    public void RaiseNetworkAddressChanged() =>
        networkAddressHandler?.Invoke(this, EventArgs.Empty);
}
