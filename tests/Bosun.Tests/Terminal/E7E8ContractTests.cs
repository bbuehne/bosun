using Bosun.Configuration;
using Bosun.SessionMonitor;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Terminal.Support;
using Bosun.Terminal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Terminal;

/// <summary>
/// The cross-epic contract test the E7 brief calls for: E7 (this file's namespace) and E8
/// (<see cref="SshCommandLineParser"/>) were built independently against the same sentence in
/// ADR-013, and this is the test that pins that they actually agree, rather than trusting they do.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed at bs-dkm, and why this suite got stronger.</b> The contract used to be "E7
/// emits the config key, E8 parses the config key back out" -- so a test could assert
/// <c>parsed == host.Key</c> against the parser alone. ADR-013 Amendment 1 makes E7 emit
/// <c>user@hostname</c>, which means the round trip is no longer a string identity: the config key
/// is recovered by *correlation against hosts.toml*, which is
/// <see cref="SshSessionMonitor"/>'s job, not the parser's. Asserting only on the parser would
/// therefore no longer test the thing that has to work. These tests run the invocation through a
/// real <see cref="SshSessionMonitor"/> with a faked process enumerator, so what is pinned is the
/// property that actually matters: <b>a live ssh.exe launched from a Bosun profile shows up in the
/// tray under the right host.</b>
/// </para>
/// <para>
/// <b>What this test feeds the parser, and why.</b> <see cref="SshCommandLineParser"/> reads a
/// live <c>ssh.exe</c> PROCESS's own command line via CIM (its class remarks are explicit about
/// this) -- never Windows Terminal's top-level profile <c>commandline</c> field. Those two are the
/// same string when <c>session.reconnect</c> is off, but diverge when it is on: the profile's
/// <c>commandline</c> becomes a <c>cmd.exe /d /c "..."</c> loop, and feeding THAT into the parser
/// returns null (its first token is <c>cmd.exe</c>, not <c>ssh</c>) -- correctly, because that is
/// not what ssh.exe's own argv will ever look like; the loop's inner ssh invocation is.
/// </para>
/// </remarks>
public sealed class E7E8ContractTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false)] // plain ssh, no reconnect
    [InlineData(false, true)] // plain ssh, reconnect
    [InlineData(true, false)] // tmux, no reconnect
    [InlineData(true, true)] // tmux, reconnect
    public void A_live_ssh_launched_from_a_profile_correlates_back_to_its_host(bool tmux, bool reconnect)
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: tmux, tmuxSession: "main", reconnect: reconnect);

        // Exactly what ssh.exe's own argv will be, whichever process launched it.
        var sshInvocation = FragmentProfileGenerator.BuildSshInvocation(host);

        var session = Assert.Single(MonitorFor(host, sshInvocation).GetActiveSessions());
        Assert.Equal(host.Key, session.HostKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_invocation_carries_the_hosts_own_connection_details_not_its_key(bool tmux)
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: tmux, tmuxSession: "main");

        var target = SshCommandLineParser.TryParse(FragmentProfileGenerator.BuildSshInvocation(host));

        Assert.NotNull(target);
        Assert.Equal(host.Hostname, target.Host);
        Assert.Equal(host.User, target.User);
        Assert.Equal(host.Port, target.Port);

        // The regression this whole change exists to prevent: the config key must never be handed
        // to ssh as something to resolve. It is Bosun's identity, not a network name.
        Assert.NotEqual(host.Key, target.Host);
    }

    [Fact]
    public void The_identity_file_is_passed_and_quoted_so_a_path_with_spaces_survives()
    {
        var host = TerminalHostFixtures.Host("example-nas") with
        {
            IdentityFile = @"C:\Program Files\keys\id_ed25519",
        };

        var invocation = FragmentProfileGenerator.BuildSshInvocation(host);

        Assert.Contains(@"-i ""C:\Program Files\keys\id_ed25519""", invocation, StringComparison.Ordinal);

        // And it must still not be mistaken for the target once tokenized back out.
        Assert.Equal(host.Hostname, SshCommandLineParser.TryParse(invocation)?.Host);
    }

    [Fact]
    public void ProfileCommandLine_correlates_directly_when_reconnect_is_off()
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: false, reconnect: false);

        var profile = FragmentProfileGenerator.CreateProfile(host);

        var session = Assert.Single(MonitorFor(host, profile.CommandLine).GetActiveSessions());
        Assert.Equal(host.Key, session.HostKey);
    }

    [Fact]
    public void ProfileCommandLine_does_NOT_correlate_directly_when_reconnect_wraps_it()
    {
        // This is the documented divergence, not a bug: the parser is designed to read a live
        // ssh.exe process's own argv (see SshCommandLineParser's class remarks), which is
        // BuildSshInvocation's output, not the wrapped Terminal commandline field. Feeding the
        // wrapped string in is expected to fail to correlate.
        var host = TerminalHostFixtures.Host("example-nas", tmux: false, reconnect: true);

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Null(SshCommandLineParser.TryParse(profile.CommandLine));

        // ...but the inner invocation the cmd.exe loop actually launches still correlates, and the
        // wrapper contains it verbatim.
        var innerInvocation = FragmentProfileGenerator.BuildSshInvocation(host);
        var session = Assert.Single(MonitorFor(host, innerInvocation).GetActiveSessions());
        Assert.Equal(host.Key, session.HostKey);
        Assert.Contains(innerInvocation, profile.CommandLine, StringComparison.Ordinal);
    }

    /// <summary>A real <see cref="SshSessionMonitor"/> over one faked live ssh.exe whose command
    /// line is <paramref name="commandLine"/>, with <paramref name="host"/> the only configured
    /// host. Nothing here touches a real process, CIM, or the TCP table.</summary>
    private static SshSessionMonitor MonitorFor(HostConfig host, string commandLine)
    {
        var enumerator = new FakeSshProcessEnumerator();
        enumerator.SetProcesses(
        [
            new SshProcessInfo
            {
                ProcessId = 100,
                Target = SshCommandLineParser.TryParse(commandLine),
                StartTime = StartTime,
            },
        ]);

        return new SshSessionMonitor(
            enumerator,
            new FakeTcpConnectionReader(),
            new FakeHostConfigStore(TerminalHostFixtures.Build(host)),
            NullLogger<SshSessionMonitor>.Instance);
    }
}
