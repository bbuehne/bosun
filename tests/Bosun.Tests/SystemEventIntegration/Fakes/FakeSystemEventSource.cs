using Bosun.SystemEventIntegration;

namespace Bosun.Tests.SystemEventIntegration.Fakes;

/// <summary>
/// The fake half of "one interface, one Win32 implementation, one fake" (bs-0wz). Every raw
/// <c>Raise*</c> method invokes the corresponding public event synchronously and deterministically
/// -- no threads, no real OS event, matching every other fake in this test suite.
/// </summary>
internal sealed class FakeSystemEventSource : ISystemEventSource
{
    public int StartCalls { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler? Suspend;
    public event EventHandler? Resume;
    public event EventHandler? NetworkAddressChanged;
    public event EventHandler? SessionLock;

    public void Start() => StartCalls++;

    public void RaiseSuspend() => Suspend?.Invoke(this, EventArgs.Empty);

    public void RaiseResume() => Resume?.Invoke(this, EventArgs.Empty);

    public void RaiseNetworkAddressChanged() => NetworkAddressChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseSessionLock() => SessionLock?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Disposed = true;
}
