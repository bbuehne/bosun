using Bosun.Configuration;
using Bosun.SessionMonitor.Interop;
using Microsoft.Extensions.Logging;

namespace Bosun.SessionMonitor;

/// <summary>
/// Real <see cref="ISessionMonitor"/> (bs-gme): composes <see cref="ISshProcessEnumerator"/>
/// (bs-8dr) and <see cref="ITcpConnectionReader"/> (bs-8je), correlating both to
/// <see cref="IHostConfigStore.Current"/> -- never by <c>display_name</c> (bs-08g). Sessions are
/// reported under a host's config key, but since bs-dkm that key is *derived* from the target ssh
/// was pointed at rather than read straight off the command line; see <see cref="ResolveHostKey"/>.
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
            .Select(p => (Process: p, HostKey: p.Target is null ? null : ResolveHostKey(p.Target, hosts)))
            .Where(m => m.HostKey is not null)
            .ToList();

        if (matched.Count == 0)
        {
            return [];
        }

        var connectionsByPid = SafeGetConnections()
            .GroupBy(c => c.ProcessId)
            .ToDictionary(g => g.Key, g => g.First());

        var sessions = new List<SshSession>(matched.Count);
        foreach (var (process, hostKey) in matched)
        {
            connectionsByPid.TryGetValue(process.ProcessId, out var connection);

            sessions.Add(new SshSession
            {
                HostKey = hostKey!,
                ProcessId = process.ProcessId,
                SocketState = connection?.State ?? SessionSocketState.Unknown,
                RemoteEndpoint = connection?.RemoteEndPoint,
                StartTime = process.StartTime,
            });
        }

        return sessions;
    }

    /// <summary>
    /// Maps a live ssh process's target back to a configured host key, or <see langword="null"/>
    /// if it is not one of ours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes have to resolve, because two things launch ssh. Bosun's own Terminal profiles
    /// emit the fully-specified <c>user@hostname</c> form (ADR-013 Amendment 1), so the usual case
    /// is matching on <see cref="HostConfig.Hostname"/>. But a user can still type
    /// <c>ssh myhost</c> against an <c>ssh_config</c> alias, and before the amendment that WAS
    /// Bosun's own form -- so an alias matching a config key is checked first and still correlates.
    /// </para>
    /// <para>
    /// <b>Ambiguity resolves to nothing, never to a guess.</b> Two hosts can share a hostname and
    /// differ only by user or port (the same box under two accounts). User and port narrow the
    /// candidates when the command line carries them, but if more than one host still matches,
    /// this returns <see langword="null"/>: showing a session under the wrong host is worse than
    /// not showing it, because the tray would then report activity on a host that has none.
    /// Narrowing is skipped when it would eliminate every candidate, since ssh fills in defaults
    /// (the local username, port 22) that the command line does not spell out.
    /// </para>
    /// </remarks>
    private static string? ResolveHostKey(SshTarget target, IReadOnlyDictionary<string, HostConfig> hosts)
    {
        if (hosts.ContainsKey(target.Host))
        {
            return target.Host;
        }

        var candidates = hosts.Values
            .Where(h => string.Equals(h.Hostname, target.Host, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (target.User is not null)
        {
            var byUser = candidates.Where(h => string.Equals(h.User, target.User, StringComparison.Ordinal)).ToList();
            if (byUser.Count > 0)
            {
                candidates = byUser;
            }
        }

        if (target.Port is not null)
        {
            var byPort = candidates.Where(h => h.Port == target.Port).ToList();
            if (byPort.Count > 0)
            {
                candidates = byPort;
            }
        }

        return candidates.Count == 1 ? candidates[0].Key : null;
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
