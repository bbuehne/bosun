using System.Net.Sockets;

namespace Bosun.Probe;

/// <summary>
/// Real <see cref="ITcpProbeTransport"/>: a bare TCP connect via <see cref="Socket"/>. No read, no
/// write, no protocol beyond the handshake — shallow probing is reachability only (Invariant I1;
/// see the remarks on <see cref="IProbe"/>).
/// </summary>
public sealed class TcpProbeTransport : ITcpProbeTransport
{
    public async Task ConnectAsync(string hostname, int port, CancellationToken cancellationToken)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(hostname, port, cancellationToken).ConfigureAwait(false);
    }
}
