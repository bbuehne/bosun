using System.Net;
using Bosun.Configuration;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Independent.RcTimeout;
using Microsoft.Extensions.Logging;
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

    // ------------------------------------------------------------------------------------------
    // Correlating the fully-specified form Bosun's own profiles emit (bs-dkm, ADR-013 Amdt. 1).
    // Before the amendment every live ssh.exe named a config key, so correlation was a dictionary
    // lookup. Now Bosun's own profiles name a HOSTNAME, and the key has to be recovered from it.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_process_naming_a_configured_hosts_hostname_is_reported_under_that_hosts_key()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, "example-nas.example.internal", "user", 22)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal("example-nas", session.HostKey);
    }

    [Fact]
    public void Hostname_correlation_is_case_insensitive_because_DNS_names_are()
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, "EXAMPLE-NAS.Example.Internal", "user", 22)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal("example-nas", session.HostKey);
    }

    [Fact]
    public void Two_hosts_on_the_same_box_are_told_apart_by_user()
    {
        // The same machine configured twice under different accounts -- the case that makes
        // hostname alone insufficient.
        var config = ConfigWith(
            HostWithKey("nas-admin") with { Hostname = "nas.example.internal", User = "root" },
            HostWithKey("nas-me") with { Hostname = "nas.example.internal", User = "barry" });

        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, "nas.example.internal", "barry", 22)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), config);

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal("nas-me", session.HostKey);
    }

    [Fact]
    public void Two_hosts_on_the_same_box_are_told_apart_by_port()
    {
        var config = ConfigWith(
            HostWithKey("box-22") with { Hostname = "box.example.internal", Port = 22 },
            HostWithKey("box-2222") with { Hostname = "box.example.internal", Port = 2222 });

        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, "box.example.internal", "user", 2222)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), config);

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal("box-2222", session.HostKey);
    }

    [Fact]
    public void An_ambiguous_match_is_reported_under_no_host_rather_than_guessed()
    {
        // Two hosts identical in every field the command line carries. Attributing the session to
        // either would show activity on a host that may have none -- worse than showing nothing.
        var config = ConfigWith(
            HostWithKey("dup-a") with { Hostname = "box.example.internal" },
            HostWithKey("dup-b") with { Hostname = "box.example.internal" });

        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, "box.example.internal", "user", 22)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), config);

        Assert.Empty(monitor.GetActiveSessions());
    }

    [Fact]
    public void Narrowing_never_eliminates_the_only_candidate()
    {
        // ssh fills in defaults the command line does not spell out (the local username, port 22),
        // so a hand-launched `ssh nas.example.internal` carries neither. The one host on that
        // hostname must still correlate rather than being narrowed away to nothing.
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, "example-nas.example.internal", user: null, port: null)]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal("example-nas", session.HostKey);
    }

    [Fact]
    public void An_ssh_config_alias_matching_a_config_key_still_correlates()
    {
        // Bosun no longer emits this shape, but a user can still type `ssh example-nas` against
        // their own ssh_config alias, and older Terminal fragments on disk still contain it until
        // the next rewrite. Dropping it would silently shrink the session list.
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses([Process(pid: 100, targetHost: "example-nas")]);
        var monitor = CreateMonitor(enumerator, new FakeTcpConnectionReader(), ConfigWithHosts("example-nas"));

        var session = Assert.Single(monitor.GetActiveSessions());
        Assert.Equal("example-nas", session.HostKey);
    }

    [Fact]
    public void A_failure_that_persists_across_an_hour_of_one_second_polls_is_logged_once_then_as_reminders()
    {
        // bs-qcs: the status read model polls every second, so each log call here is a line per second.
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.ThrowOnEnumerate(new InvalidOperationException("CIM unavailable"));
        var tcp = new FakeTcpConnectionReader();
        var time = new FakeTimeProvider();
        var log = new LevelRecordingLogger<SshSessionMonitor>();
        var monitor = new SshSessionMonitor(enumerator, tcp, new FakeHostConfigStore(ConfigWithHosts("example-nas")), log, time);

        for (var second = 0; second < 3600; second++)
        {
            Assert.Empty(monitor.GetActiveSessions());
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Single(log.Entries, e => e.Message.Contains("ssh.exe enumeration failed", StringComparison.Ordinal));
        Assert.Equal(5, log.Entries.Count(e => e.Message.Contains("still failing", StringComparison.Ordinal)));
        Assert.Equal(6, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.True(e.Level >= LogLevel.Warning));
    }

    private static SshSessionMonitor CreateMonitor(
        ISshProcessEnumerator enumerator, ITcpConnectionReader tcp, BosunConfig config) =>
        new(enumerator, tcp, new FakeHostConfigStore(config), NullLogger<SshSessionMonitor>.Instance);

    /// <summary>A live ssh.exe whose command line named <paramref name="targetHost"/> as its
    /// target, with no user or port given -- i.e. the hand-launched <c>ssh sometarget</c> shape.
    /// Correlation of the fully-specified form Bosun itself emits is covered separately below.
    /// </summary>
    private static SshProcessInfo Process(int pid, string? targetHost) => new()
    {
        ProcessId = pid,
        Target = targetHost is null ? null : new SshTarget { Host = targetHost },
        StartTime = StartTime,
    };

    private static SshProcessInfo Process(int pid, string host, string? user, int? port) => new()
    {
        ProcessId = pid,
        Target = new SshTarget { Host = host, User = user, Port = port },
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

    private static BosunConfig ConfigWith(params HostConfig[] hosts) => new()
    {
        Global = new GlobalConfig(),
        Hosts = hosts.ToDictionary(h => h.Key, StringComparer.Ordinal),
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
