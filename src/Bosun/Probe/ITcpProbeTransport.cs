namespace Bosun.Probe;

/// <summary>
/// The socket-level seam behind the shallow probe. Deliberately minimal — "attempt a TCP
/// connect, honour cancellation" is everything <see cref="HostProbe"/> needs; the timeout itself
/// is applied by the caller via a <see cref="TimeProvider"/>-driven cancellation, not by this
/// interface (see <see cref="HostProbe"/>). CLAUDE.md worktree safety: no test in the default
/// suite may touch a real socket, so this is faked in tests
/// (tests/Bosun.Tests/Probe/Fakes/FakeTcpProbeTransport.cs) — production wiring is
/// <see cref="TcpProbeTransport"/>.
/// </summary>
public interface ITcpProbeTransport
{
    /// <summary>
    /// Attempts a TCP connect to <paramref name="hostname"/>:<paramref name="port"/>. Completes
    /// on success; throws on failure so <see cref="HostProbe"/> can classify the failure kind from
    /// the exception (mirrors real <see cref="System.Net.Sockets.Socket"/>/DNS exception shapes).
    /// </summary>
    Task ConnectAsync(string hostname, int port, CancellationToken cancellationToken);
}
