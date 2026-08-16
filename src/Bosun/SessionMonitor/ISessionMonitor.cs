using System.Net;

namespace Bosun.SessionMonitor;

/// <summary>
/// Enumerates live SSH sessions and correlates them to configured hosts (docs/ARCHITECTURE.md
/// §3, bs-gme). This is the one component with meaningful P/Invoke (bs-8je) and CIM interop
/// (bs-8dr); every consumer -- including E9's tray status -- sees only this snapshot API and
/// never needs to know how enumeration works.
/// </summary>
/// <remarks>
/// Correlation is to <see cref="Configuration.HostConfig.Key"/>, never
/// <see cref="Configuration.HostConfig.DisplayName"/> (bs-08g: the key is the stable identity
/// used throughout the system, including session correlation). A live <c>ssh.exe</c> process
/// that does not match any configured host's key is not surfaced here and is not treated as an
/// error -- see bs-8dr.
/// </remarks>
public interface ISessionMonitor
{
    /// <summary>
    /// A snapshot of every live <c>ssh.exe</c> process that correlates to a configured host, at
    /// the moment of the call. Cheap enough to poll (E9 does, to paint the tray) but not free --
    /// each call re-enumerates processes and re-reads the TCP table.
    /// </summary>
    IReadOnlyList<SshSession> GetActiveSessions();
}

/// <summary>One live, correlated SSH session.</summary>
public sealed record SshSession
{
    /// <summary>The config key (<c>[hosts.&lt;key&gt;]</c>) this session was correlated to --
    /// never <see cref="Configuration.HostConfig.DisplayName"/>.</summary>
    public required string HostKey { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>TCP connection state for this process's ssh connection, from
    /// <c>GetExtendedTcpTable</c>. <see cref="SessionSocketState.Unknown"/> when no matching TCP
    /// row could be found (e.g. the process just started, or the TCP table read failed).</summary>
    public required SessionSocketState SocketState { get; init; }

    /// <summary>The remote address:port this session is connected to, if a matching TCP row was
    /// found.</summary>
    public IPEndPoint? RemoteEndpoint { get; init; }

    public required DateTimeOffset StartTime { get; init; }
}

/// <summary>Mirrors the <c>MIB_TCP_STATE</c> values reported by <c>GetExtendedTcpTable</c>
/// (bs-8je), plus <see cref="Unknown"/> for "no TCP row could be found for this process".</summary>
public enum SessionSocketState
{
    Unknown = 0,
    Closed,
    Listen,
    SynSent,
    SynReceived,
    Established,
    FinWait1,
    FinWait2,
    CloseWait,
    Closing,
    LastAck,
    TimeWait,
    DeleteTcb,
}
