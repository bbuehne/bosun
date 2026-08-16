using Bosun.Probe;

namespace Bosun.Tests.Probe.Fakes;

/// <summary>
/// Fake <see cref="IRemoteRootLister"/> — E4 must not block on E3's real rclone-backed
/// implementation, and the default test suite must not reach a real rclone rc anyway (CLAUDE.md
/// worktree safety). Same three behaviours as <see cref="FakeTcpProbeTransport"/>: succeed,
/// throw, or hang until cancelled.
/// </summary>
internal sealed class FakeRemoteRootLister : IRemoteRootLister
{
    private Exception? _throws;
    private bool _hangs;

    /// <summary>Every <c>hostKey</c> this fake was asked to list, in call order.</summary>
    public List<string> Requests { get; } = [];

    public void SucceedsImmediately()
    {
        _throws = null;
        _hangs = false;
    }

    public void ThrowsImmediately(Exception exception)
    {
        _throws = exception;
        _hangs = false;
    }

    public void Hangs()
    {
        _throws = null;
        _hangs = true;
    }

    public async Task ListRootAsync(string hostKey, CancellationToken cancellationToken)
    {
        Requests.Add(hostKey);

        if (_throws is not null)
        {
            throw _throws;
        }

        if (_hangs)
        {
            var tcs = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task.ConfigureAwait(false);
        }
    }
}
