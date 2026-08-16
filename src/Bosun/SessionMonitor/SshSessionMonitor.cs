using Bosun.Configuration;
using Bosun.SessionMonitor.Interop;
using Microsoft.Extensions.Logging;

namespace Bosun.SessionMonitor;

/// <summary>
/// Real <see cref="ISessionMonitor"/> (bs-gme): composes <see cref="ISshProcessEnumerator"/>
/// (bs-8dr) and <see cref="ITcpConnectionReader"/> (bs-8je), correlating both to
/// <see cref="IHostConfigStore.Current"/> by config key (bs-08g) -- never <c>display_name</c>.
/// </summary>
/// <remarks>
/// Every dependency here can fail independently at runtime -- CIM can be slow or unavailable,
/// the TCP table read can fail -- and none of that should crash E9's polling loop. Both calls
/// are guarded: enumeration failure means "no sessions this tick"; TCP table failure means
/// "sessions are reported with <see cref="SessionSocketState.Unknown"/>", never a thrown
/// exception reaching the caller.
/// </remarks>
public sealed class SshSessionMonitor(
    ISshProcessEnumerator processEnumerator,
    ITcpConnectionReader tcpConnectionReader,
    IHostConfigStore configStore,
    ILogger<SshSessionMonitor> logger) : ISessionMonitor
{
    public IReadOnlyList<SshSession> GetActiveSessions()
    {
        var processes = SafeEnumerateProcesses();
        if (processes.Count == 0)
        {
            return [];
        }

        var hosts = configStore.Current.Hosts;
        var matched = processes
            .Where(p => p.TargetHost is not null && hosts.ContainsKey(p.TargetHost))
            .ToList();

        if (matched.Count == 0)
        {
            return [];
        }

        var connectionsByPid = SafeGetConnections()
            .GroupBy(c => c.ProcessId)
            .ToDictionary(g => g.Key, g => g.First());

        var sessions = new List<SshSession>(matched.Count);
        foreach (var process in matched)
        {
            connectionsByPid.TryGetValue(process.ProcessId, out var connection);

            sessions.Add(new SshSession
            {
                HostKey = process.TargetHost!,
                ProcessId = process.ProcessId,
                SocketState = connection?.State ?? SessionSocketState.Unknown,
                RemoteEndpoint = connection?.RemoteEndPoint,
                StartTime = process.StartTime,
            });
        }

        return sessions;
    }

    private IReadOnlyList<SshProcessInfo> SafeEnumerateProcesses()
    {
        try
        {
            return processEnumerator.Enumerate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "ssh.exe enumeration failed; reporting no sessions this tick");
            return [];
        }
    }

    private IReadOnlyList<TcpConnectionInfo> SafeGetConnections()
    {
        try
        {
            return tcpConnectionReader.GetConnections();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "TCP table read failed; sessions will report Unknown socket state this tick");
            return [];
        }
    }
}
