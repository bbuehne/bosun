using Bosun.Hosting;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// Records calls instead of taking a real named <see cref="System.Threading.Mutex"/>. Every
/// <see cref="SingleInstanceOrchestrator"/> and <see cref="BootstrapOrchestrator"/> test uses this
/// rather than <see cref="MutexSingleInstanceGuard"/> so tests never race a real,
/// concurrently-running Bosun (or a concurrently-running test) for the same named OS object.
/// </summary>
public sealed class FakeSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly bool _acquireResult;

    /// <param name="acquireResult">What <see cref="TryAcquire"/> returns (and sets <see cref="IsOwned"/>
    /// to) when called.</param>
    /// <param name="isOwned">The initial value of <see cref="IsOwned"/>, for tests (e.g.
    /// <c>BootstrapOrchestratorTests</c>) that need to assert against an already-primary guard
    /// without going through <see cref="TryAcquire"/> first.</param>
    public FakeSingleInstanceGuard(bool acquireResult = false, bool isOwned = false)
    {
        _acquireResult = acquireResult;
        IsOwned = isOwned;
    }

    public bool IsOwned { get; private set; }

    public int TryAcquireCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public bool TryAcquire()
    {
        TryAcquireCallCount++;
        IsOwned = _acquireResult;
        return _acquireResult;
    }

    public void Dispose()
    {
        DisposeCallCount++;
        IsOwned = false;
    }
}
