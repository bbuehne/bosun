using Bosun.SessionMonitor;

namespace Bosun.Tests.SessionMonitor;

/// <summary>
/// Covers bs-8dr's acceptance criterion directly: command-line parsing is a pure function,
/// exhaustively tested over strings -- every form E7 emits, plus malformed input a hand-launched
/// or half-typed <c>ssh</c> invocation could produce.
/// </summary>
public sealed class SshCommandLineParserTests
{
    [Theory]
    [InlineData("ssh myhost", "myhost")]
    [InlineData("ssh example-nas", "example-nas")]
    [InlineData("ssh.exe myhost", "myhost")]
    [InlineData(@"C:\Windows\System32\OpenSSH\ssh.exe myhost", "myhost")]
    [InlineData("\"C:\\Program Files\\OpenSSH\\ssh.exe\" myhost", "myhost")]
    public void Parses_the_plain_form_E7_emits(string commandLine, string expectedHost)
    {
        Assert.Equal(expectedHost, SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Theory]
    [InlineData("ssh -t myhost tmux new -A -s main", "myhost")]
    [InlineData("ssh.exe -t example-nas tmux new -A -s main", "example-nas")]
    [InlineData("\"C:\\Program Files\\OpenSSH\\ssh.exe\" -t myhost tmux new -A -s main", "myhost")]
    public void Parses_the_tmux_form_E7_emits(string commandLine, string expectedHost)
    {
        Assert.Equal(expectedHost, SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Fact]
    public void Reconnect_wrapper_is_irrelevant_because_it_never_appears_in_sshexes_own_command_line()
    {
        // E7's reconnect loop is a shell wrapper AROUND the ssh invocation. The ssh.exe process
        // itself -- the one CIM reports on -- is still launched with exactly one of the two
        // known forms. There is nothing extra for the parser to see or strip.
        Assert.Equal("myhost", SshCommandLineParser.TryParseTargetHost("ssh myhost"));
        Assert.Equal("myhost", SshCommandLineParser.TryParseTargetHost("ssh -t myhost tmux new -A -s main"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Null_or_blank_command_line_is_not_a_match(string? commandLine)
    {
        Assert.Null(SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("ssh.exe")]
    [InlineData("ssh -t")]
    [InlineData("ssh.exe -t")]
    public void Bare_invocation_with_no_host_token_is_not_a_match(string commandLine)
    {
        Assert.Null(SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("explorer.exe myhost")]
    [InlineData("rsync myhost")]
    [InlineData("sshd myhost")] // sshd, not ssh -- must not fuzzy-match
    public void Non_ssh_executable_is_not_a_match(string commandLine)
    {
        Assert.Null(SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Theory]
    [InlineData("ssh -p 2200 myhost")] // hand-launched with flags E7 never emits
    [InlineData("ssh -vvv myhost")]
    [InlineData("ssh -t -t myhost")] // malformed double -t
    public void Hand_launched_ssh_with_unrecognised_flags_is_not_an_error_just_not_a_match(string commandLine)
    {
        // bs-8dr: an ssh.exe launched by hand that doesn't match one of E7's two forms is not an
        // error. It's simply not correlated to a host.
        Assert.Null(SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Fact]
    public void User_at_host_form_is_extracted_literally_without_validating_against_config()
    {
        // The parser has no knowledge of configured hosts -- it just extracts the token. Whether
        // "user@myhost" matches a configured key is SshSessionMonitor's concern, not this one's.
        Assert.Equal("user@myhost", SshCommandLineParser.TryParseTargetHost("ssh user@myhost"));
    }

    [Theory]
    [InlineData("ssh    myhost", "myhost")]
    [InlineData("  ssh myhost  ", "myhost")]
    [InlineData("ssh\tmyhost", "myhost")]
    public void Extra_whitespace_does_not_change_the_result(string commandLine, string expectedHost)
    {
        Assert.Equal(expectedHost, SshCommandLineParser.TryParseTargetHost(commandLine));
    }

    [Fact]
    public void Unterminated_quote_does_not_throw_and_is_not_a_match()
    {
        // The unterminated quote swallows the rest of the line -- including the whitespace that
        // would normally separate the exe from its argument -- into one token, so the
        // executable-name check fails cleanly rather than throwing.
        var result = SshCommandLineParser.TryParseTargetHost("\"C:\\ssh.exe myhost");

        Assert.Null(result);
    }
}
