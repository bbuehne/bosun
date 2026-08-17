using Bosun.Hosting;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// Records calls instead of taking a real named <see cref="System.Threading.Mutex"/>. Every
/// <see cref="SingleInstanceOrchestrator"/> test uses this rather than
/// <see cref="MutexSingleInstanceGuard"/> so tests never race a real, concurrently-running Bosun
/// (or a concurrently-running test) for the same named OS object.
/// </summary>
public sealed class FakeSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly bool _acquireResult;

    public FakeSingleInstanceGuard(bool acquireResult)
    {
        _acquireResult = acquireResult;
    }

    public int TryAcquireCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public bool TryAcquire()
    {
        TryAcquireCallCount++;
        return _acquireResult;
    }

    public void Dispose()
    {
        DisposeCallCount++;
    }
}
