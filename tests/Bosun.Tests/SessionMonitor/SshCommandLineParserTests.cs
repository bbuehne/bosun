using Bosun.SessionMonitor;

namespace Bosun.Tests.SessionMonitor;

/// <summary>
/// Covers bs-8dr's acceptance criterion directly: command-line parsing is a pure function,
/// exhaustively tested over strings -- every form E7 emits, plus malformed input a hand-launched
/// or half-typed <c>ssh</c> invocation could produce.
/// </summary>
/// <remarks>
/// Reworked for bs-dkm. E7 now emits the fully-specified
/// <c>ssh -i "&lt;key&gt;" -p &lt;port&gt; &lt;user&gt;@&lt;host&gt;</c> form (ADR-013 Amendment 1),
/// so the flag-bearing command lines this suite previously asserted were NOT matches are now
/// exactly what Bosun itself produces. Those cases flipped deliberately -- see
/// <see cref="Flag_bearing_invocations_now_parse_rather_than_being_rejected"/>.
/// </remarks>
public sealed class SshCommandLineParserTests
{
    [Theory]
    [InlineData("ssh myhost", "myhost")]
    [InlineData("ssh example-nas", "example-nas")]
    [InlineData("ssh.exe myhost", "myhost")]
    [InlineData(@"C:\Windows\System32\OpenSSH\ssh.exe myhost", "myhost")]
    [InlineData("\"C:\\Program Files\\OpenSSH\\ssh.exe\" myhost", "myhost")]
    public void Parses_a_bare_target(string commandLine, string expectedHost)
    {
        var target = SshCommandLineParser.TryParse(commandLine);

        Assert.NotNull(target);
        Assert.Equal(expectedHost, target.Host);
        Assert.Null(target.User);
        Assert.Null(target.Port);
    }

    [Fact]
    public void Parses_the_fully_specified_form_E7_emits()
    {
        var target = SshCommandLineParser.TryParse(
            "ssh -i \"C:\\Users\\me\\.ssh\\id_ed25519\" -p 2222 ubuntu@www.example.com");

        Assert.NotNull(target);
        Assert.Equal("www.example.com", target.Host);
        Assert.Equal("ubuntu", target.User);
        Assert.Equal(2222, target.Port);
    }

    [Fact]
    public void Parses_the_fully_specified_tmux_form_E7_emits()
    {
        var target = SshCommandLineParser.TryParse(
            "ssh -t -i \"~/.ssh/id_ed25519\" -p 22 someuser@nas.example.internal tmux new -A -s main");

        Assert.NotNull(target);
        Assert.Equal("nas.example.internal", target.Host);
        Assert.Equal("someuser", target.User);
        Assert.Equal(22, target.Port);
    }

    [Fact]
    public void A_key_path_containing_spaces_is_not_mistaken_for_the_target()
    {
        // The quoted path is one argv entry. A tokenizer that split on whitespace would take
        // "Files\my" as the target and correlate this session to nothing.
        var target = SshCommandLineParser.TryParse(
            "ssh -i \"C:\\Program Files\\my key\" -p 22 ubuntu@www.example.com");

        Assert.NotNull(target);
        Assert.Equal("www.example.com", target.Host);
    }

    [Theory]
    [InlineData("ssh -p 2200 myhost", "myhost", 2200)]
    [InlineData("ssh -vvv myhost", "myhost", null)]
    [InlineData("ssh -t -t myhost", "myhost", null)]
    [InlineData("ssh -J bastion.example.com myhost", "myhost", null)]
    [InlineData("ssh -o StrictHostKeyChecking=no myhost", "myhost", null)]
    [InlineData("ssh -4 -C myhost", "myhost", null)]
    public void Flag_bearing_invocations_now_parse_rather_than_being_rejected(
        string commandLine, string expectedHost, int? expectedPort)
    {
        // Before bs-dkm these all returned null: the parser accepted two rigid shapes and treated
        // any unknown flag as "not one of E7's forms". That was safe only while E7 emitted no
        // flags. It emits its own now, and a rule of "reject what I don't recognise" would drop a
        // hand-launched `ssh -J bastion myhost` out of the session list for no good reason.
        var target = SshCommandLineParser.TryParse(commandLine);

        Assert.NotNull(target);
        Assert.Equal(expectedHost, target.Host);
        Assert.Equal(expectedPort, target.Port);
    }

    [Fact]
    public void A_value_taking_flags_argument_is_never_mistaken_for_the_target()
    {
        // The single most damaging way to get this wrong: treat "key" as the host.
        var target = SshCommandLineParser.TryParse("ssh -i key myhost");

        Assert.NotNull(target);
        Assert.Equal("myhost", target.Host);
    }

    [Fact]
    public void A_value_taking_flag_with_no_argument_is_not_a_match()
    {
        Assert.Null(SshCommandLineParser.TryParse("ssh -i"));
        Assert.Null(SshCommandLineParser.TryParse("ssh -p"));
    }

    [Fact]
    public void Tokens_after_the_target_are_the_remote_command_and_are_not_read_as_flags()
    {
        // ssh takes everything after the target as the command to run remotely. A "-p" there is
        // an argument to that command, not a port for ssh -- reading it as one would be wrong.
        var target = SshCommandLineParser.TryParse("ssh myhost some-remote-tool -p 9999");

        Assert.NotNull(target);
        Assert.Equal("myhost", target.Host);
        Assert.Null(target.Port);
    }

    [Fact]
    public void Reconnect_wrapper_is_irrelevant_because_it_never_appears_in_sshexes_own_command_line()
    {
        // E7's reconnect loop is a shell wrapper AROUND the ssh invocation. The ssh.exe process
        // itself -- the one CIM reports on -- is still launched with the invocation verbatim.
        // There is nothing extra for the parser to see or strip.
        Assert.Equal("myhost", SshCommandLineParser.TryParse("ssh myhost")?.Host);
        Assert.Equal("myhost", SshCommandLineParser.TryParse("ssh -t myhost tmux new -A -s main")?.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Null_or_blank_command_line_is_not_a_match(string? commandLine)
    {
        Assert.Null(SshCommandLineParser.TryParse(commandLine));
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("ssh.exe")]
    [InlineData("ssh -t")]
    [InlineData("ssh.exe -t")]
    [InlineData("ssh -4")]
    public void Bare_invocation_with_no_host_token_is_not_a_match(string commandLine)
    {
        Assert.Null(SshCommandLineParser.TryParse(commandLine));
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("explorer.exe myhost")]
    [InlineData("rsync myhost")]
    [InlineData("sshd myhost")] // sshd, not ssh -- must not fuzzy-match
    public void Non_ssh_executable_is_not_a_match(string commandLine)
    {
        Assert.Null(SshCommandLineParser.TryParse(commandLine));
    }

    [Fact]
    public void An_inline_user_is_split_from_the_host()
    {
        var target = SshCommandLineParser.TryParse("ssh user@myhost");

        Assert.NotNull(target);
        Assert.Equal("myhost", target.Host);
        Assert.Equal("user", target.User);
    }

    [Fact]
    public void A_dash_l_user_is_picked_up_and_an_inline_user_wins_over_it()
    {
        Assert.Equal("alice", SshCommandLineParser.TryParse("ssh -l alice myhost")?.User);

        // ssh's own precedence: the target's user@ beats -l.
        Assert.Equal("bob", SshCommandLineParser.TryParse("ssh -l alice bob@myhost")?.User);
    }

    [Fact]
    public void A_target_that_is_only_an_at_sign_is_not_a_match()
    {
        Assert.Null(SshCommandLineParser.TryParse("ssh user@"));
    }

    [Fact]
    public void A_non_numeric_port_is_ignored_rather_than_throwing()
    {
        var target = SshCommandLineParser.TryParse("ssh -p notaport myhost");

        Assert.NotNull(target);
        Assert.Equal("myhost", target.Host);
        Assert.Null(target.Port);
    }

    [Theory]
    [InlineData("ssh    myhost", "myhost")]
    [InlineData("  ssh myhost  ", "myhost")]
    [InlineData("ssh\tmyhost", "myhost")]
    public void Extra_whitespace_does_not_change_the_result(string commandLine, string expectedHost)
    {
        Assert.Equal(expectedHost, SshCommandLineParser.TryParse(commandLine)?.Host);
    }

    [Fact]
    public void Unterminated_quote_does_not_throw_and_is_not_a_match()
    {
        // The unterminated quote swallows the rest of the line -- including the whitespace that
        // would normally separate the exe from its argument -- into one token, so the
        // executable-name check fails cleanly rather than throwing.
        var result = SshCommandLineParser.TryParse("\"C:\\ssh.exe myhost");

        Assert.Null(result);
    }
}
