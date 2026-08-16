using Bosun.Probe;

namespace Bosun.Tests.Probe.Fakes;

/// <summary>
/// Fake <see cref="ITcpProbeTransport"/> — no default-suite test may touch a real socket
/// (CLAUDE.md worktree safety). Supports three configurable behaviours:
/// <see cref="SucceedsImmediately"/>, <see cref="ThrowsImmediately"/> (to drive
/// <see cref="HostProbe"/>'s exception-classification logic with realistic exception shapes), and
/// <see cref="Hangs"/> (never completes on its own — used to exercise
/// <see cref="HostProbe"/>'s real <see cref="TimeProvider"/>-driven timeout/cancellation wiring,
/// rather than faking the timeout outcome directly).
/// </summary>
internal sealed class FakeTcpProbeTransport : ITcpProbeTransport
{
    private Exception? _throws;
    private bool _hangs;

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

    public async Task ConnectAsync(string hostname, int port, CancellationToken cancellationToken)
    {
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
