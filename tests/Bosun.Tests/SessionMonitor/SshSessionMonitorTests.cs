using System.Net;
using Bosun.Configuration;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;
using Bosun.Tests.SessionMonitor.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.SessionMonitor;

/// <summary>
/// Covers bs-gme's wiring: <see cref="SshSessionMonitor"/> correlates
/// <see cref="ISshProcessEnumerator"/> output to <see cref="IHostConfigStore.Current"/> by config
/// key (bs-08g), joins in TCP state from <see cref="ITcpConnectionReader"/>, and degrades
/// gracefully -- never throws -- when either collaborator fails (bs-8dr, bs-8je). Both
/// collaborators are faked; nothing here touches a real process, CIM, or the TCP table.
/// </summary>
public sealed class SshSessionMonitorTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_process_matching_a_configured_host_key_is_reported()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "example-nas")]);
        var tcp = new FakeTcpConnectionReader();
        var monitor = CreateMonitor(enumerator, tcp, ConfigWithHosts("example-nas"));

        var sessions = monitor.GetActiveSessions();

        var session = Assert.Single(sessions);
        Assert.Equal("example-nas", session.HostKey);
        Assert.Equal(100, session.ProcessId);
        Assert.Equal(StartTime, session.StartTime);
    }

    [Fact]
    public void A_process_with_no_target_host_is_not_reported()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: null)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        Assert.Empty(monitor.GetActiveSessions());
    }

    [Fact]
    public void A_hand_launched_ssh_that_matches_no_configured_host_is_not_reported()
    {
        // bs-8dr: not an error, just not surfaced. E.g. the user typed "ssh some-random-box".
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "some-random-box")]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        Assert.Empty(monitor.GetActiveSessions());
    }

    [Fact]
    public void Correlation_is_by_config_key_and_is_case_sensitive_to_the_literal_key()
    {
        // bs-08g: correlation is to the config KEY, never display_name. This also pins down
        // that "Example-NAS" (wrong case) does not silently match "example-nas".
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "Example-NAS")]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        Assert.Empty(monitor.GetActiveSessions());
    }

    [Fact]
    public void Socket_state_and_remote_endpoint_are_joined_in_by_process_id()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "example-nas")]);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 22);
        var tcp = new FakeTcpConnectionReader();
        tcp.SetConnections([Connection(pid: 100, SessionSocketState.Established, remote)]);
        var monitor = CreateMonitor(enumerator, tcp, ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal(SessionSocketState.Established, session.SocketState);
        Assert.Equal(remote, session.RemoteEndpoint);
    }

    [Fact]
    public void No_matching_TCP_row_reports_Unknown_socket_state_rather_than_omitting_the_session()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "example-nas")]);
        var tcp = new FakeTcpConnectionReader();
        tcp.SetConnections([Connection(pid: 999, SessionSocketState.Established, new IPEndPoint(IPAddress.Loopback, 22))]);
        var monitor = CreateMonitor(enumerator, tcp, ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal(SessionSocketState.Unknown, session.SocketState);
        Assert.Null(session.RemoteEndpoint);
    }

    [Fact]
    public void Enumeration_failure_reports_no_sessions_rather_than_throwing()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.ThrowOnEnumerate(new InvalidOperationException("CIM unavailable"));
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        var sessions = monitor.GetActiveSessions();

        Assert.Empty(sessions);
    }

    [Fact]
    public void Tcp_table_read_failure_still_reports_the_session_with_Unknown_state_rather_than_throwing()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "example-nas")]);
        var tcp = new FakeTcpConnectionReader();
        tcp.ThrowOnGetConnections(new InvalidOperationException("GetExtendedTcpTable did not stabilize"));
        var monitor = CreateMonitor(enumerator, tcp, ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal(SessionSocketState.Unknown, session.SocketState);
    }

    [Fact]
    public void Multiple_matched_processes_each_produce_a_session()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses(
        [
            Process(pid: 100, targetHost: "example-nas"),
            Process(pid: 200, targetHost: "example-remote"),
            Process(pid: 300, targetHost: "not-configured"),
        ]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas", "example-remote"));

        var sessions = monitor.GetActiveSessions();

        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.HostKey == "example-nas" && s.ProcessId == 100);
        Assert.Contains(sessions, s => s.HostKey == "example-remote" && s.ProcessId == 200);
    }

    private static SshSessionMonitor CreateMonitor(
        ISshProcessEnumerator enumerator, ITcpConnectionReader tcp, BosunConfig config) =>
        new(enumerator, tcp, new FakeHostConfigStore(config), NullLogger<SshSessionMonitor>.Instance);

    private static SshProcessInfo Process(int pid, string? targetHost) => new()
    {
        ProcessId = pid,
        TargetHost = targetHost,
        StartTime = StartTime,
    };

    private static TcpConnectionInfo Connection(int pid, SessionSocketState state, IPEndPoint remote) => new()
    {
        ProcessId = pid,
        State = state,
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 51000),
        RemoteEndPoint = remote,
    };

    private static BosunConfig ConfigWithHosts(params string[] keys) => new()
    {
        Global = new GlobalConfig(),
        Hosts = keys.ToDictionary(k => k, HostWithKey),
    };

    private static HostConfig HostWithKey(string key) => new()
    {
        Key = key,
        DisplayName = key,
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "user",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = new MountConfig { Mode = MountMode.None },
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = false,
            Tmux = false,
            TabColor = "#000000",
            ColorScheme = "Campbell",
        },
        Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = false },
    };
}
