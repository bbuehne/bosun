using Bosun.SessionMonitor;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Support;

namespace Bosun.Tests.Terminal;

/// <summary>
/// The cross-epic contract test the E7 brief calls for: E7 (this file's namespace) and E8
/// (<see cref="SshCommandLineParser"/>) were built independently against the same sentence in
/// ADR-013 -- "the commandline uses the host's config key" -- and this is the test that pins that
/// they actually agree, rather than trusting they do.
/// </summary>
/// <remarks>
/// <b>What this test feeds the parser, and why.</b> <see cref="SshCommandLineParser"/> reads a
/// live <c>ssh.exe</c> PROCESS's own command line via CIM (its class remarks are explicit about
/// this) -- never Windows Terminal's top-level profile <c>commandline</c> field. Those two are the
/// same string when <c>session.reconnect</c> is off, but diverge when it is on: the profile's
/// <c>commandline</c> becomes a <c>cmd.exe /d /c "..."</c> loop, and feeding THAT into the parser
/// returns null (its first token is <c>cmd.exe</c>, not <c>ssh</c>) -- correctly, because that is
/// not what ssh.exe's own argv will ever look like; the loop's inner ssh invocation is. This suite
/// therefore exercises both:
/// <list type="bullet">
/// <item><see cref="FragmentProfileGenerator.BuildSshInvocation"/> (what ssh.exe is actually
/// launched with, always) round-trips through the parser, with and without reconnect.</item>
/// <item><see cref="FragmentProfile.CommandLine"/> (the profile's top-level field) round-trips
/// only when reconnect is off, and is documented, not silently assumed, to NOT round-trip when
/// reconnect is on.</item>
/// </list>
/// <b>Finding:</b> the reconnect wrapper does not change what E8 sees from a live ssh.exe process
/// (the two-layer design is deliberate, and correct), but it DOES mean nothing may ever feed
/// <see cref="FragmentProfile.CommandLine"/> itself into <see cref="SshCommandLineParser"/> and
/// expect a match once reconnect is in play -- <see cref="Bosun.SessionMonitor.ISessionMonitor"/>
/// already does the right thing here because it only ever looks at real <c>ssh.exe</c> processes'
/// own command lines, never at a Terminal profile's <c>commandline</c> field, so this is not a bug
/// in either epic -- just a contract worth pinning explicitly.
/// </remarks>
public sealed class E7E8ContractTests
{
    [Theory]
    [InlineData(false, false)] // plain ssh, no reconnect
    [InlineData(false, true)] // plain ssh, reconnect
    [InlineData(true, false)] // tmux, no reconnect
    [InlineData(true, true)] // tmux, reconnect
    public void SshInvocation_always_correlates_back_to_the_config_key_regardless_of_reconnect(bool tmux, bool reconnect)
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: tmux, tmuxSession: "main", reconnect: reconnect);

        var sshInvocation = FragmentProfileGenerator.BuildSshInvocation(host);
        var parsedHost = SshCommandLineParser.TryParseTargetHost(sshInvocation);

        Assert.Equal(host.Key, parsedHost);
    }

    [Fact]
    public void ProfileCommandLine_correlates_directly_when_reconnect_is_off()
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: false, reconnect: false);

        var profile = FragmentProfileGenerator.CreateProfile(host);
        var parsedHost = SshCommandLineParser.TryParseTargetHost(profile.CommandLine);

        Assert.Equal(host.Key, parsedHost);
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
        var parsedHost = SshCommandLineParser.TryParseTargetHost(profile.CommandLine);

        Assert.Null(parsedHost);

        // ...but the inner invocation the cmd.exe loop actually launches still correlates:
        var innerInvocation = FragmentProfileGenerator.BuildSshInvocation(host);
        Assert.Equal(host.Key, SshCommandLineParser.TryParseTargetHost(innerInvocation));
        Assert.Contains(innerInvocation, profile.CommandLine);
    }
}
