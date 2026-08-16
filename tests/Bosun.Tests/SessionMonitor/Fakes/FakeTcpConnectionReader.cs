using Bosun.SessionMonitor.Interop;

namespace Bosun.Tests.SessionMonitor.Fakes;

/// <summary>Fake <see cref="ITcpConnectionReader"/> so <see cref="Bosun.SessionMonitor.SshSessionMonitor"/>'s
/// correlation logic can be tested without a real <c>GetExtendedTcpTable</c> call.</summary>
internal sealed class FakeTcpConnectionReader : ITcpConnectionReader
{
    private IReadOnlyList<TcpConnectionInfo> _connections = [];
    private Exception? _throws;

    public void SetConnections(IReadOnlyList<TcpConnectionInfo> connections) => _connections = connections;

    /// <summary>Makes the next (and every subsequent) call to <see cref="GetConnections"/> throw
    /// <paramref name="exception"/> -- simulates the TCP table read failing.</summary>
    public void ThrowOnGetConnections(Exception exception) => _throws = exception;

    public IReadOnlyList<TcpConnectionInfo> GetConnections()
    {
        if (_throws is not null)
        {
            throw _throws;
        }

        return _connections;
    }
}
