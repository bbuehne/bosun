using System.Globalization;
using System.IO;
using System.Text;

namespace Bosun.SessionMonitor;

/// <summary>
/// What an <c>ssh.exe</c> command line was aimed at, before correlation to a configured host
/// (bs-8dr, bs-dkm). <see cref="Host"/> is whichever token ssh itself would treat as the target:
/// either a hostname/IP, or an <c>ssh_config</c> alias -- this type does not and cannot know
/// which, because only <c>hosts.toml</c> can settle it. Resolving that is
/// <see cref="SshSessionMonitor"/>'s job.
/// </summary>
public sealed record SshTarget
{
    /// <summary>The target token with any <c>user@</c> prefix stripped.</summary>
    public required string Host { get; init; }

    /// <summary>From <c>user@host</c> or <c>-l user</c>; <see langword="null"/> if neither was
    /// given (ssh would then default to the local username).</summary>
    public string? User { get; init; }

    /// <summary>From <c>-p</c>; <see langword="null"/> if not given (ssh would then default to 22,
    /// or to whatever <c>ssh_config</c> says -- so absence must not be read as "22").</summary>
    public int? Port { get; init; }
}

/// <summary>
/// Pure parsing of an <c>ssh.exe</c> command line into its target (bs-8dr). No process, CIM, or
/// config access -- this is the seam that can be unit-tested exhaustively without touching a real
/// OS, per docs/ARCHITECTURE.md §3.
/// </summary>
/// <remarks>
/// <para>
/// Recognises the form E7 emits (ADR-013 as amended by bs-dkm):
/// <code>
/// ssh -i &lt;identity&gt; -p &lt;port&gt; &lt;user&gt;@&lt;hostname&gt;
/// ssh -t -i &lt;identity&gt; -p &lt;port&gt; &lt;user&gt;@&lt;hostname&gt; tmux new -A -s &lt;session&gt;
/// </code>
/// and, deliberately, a good deal more besides. Before bs-dkm this parser accepted exactly two
/// rigid shapes and rejected anything carrying a flag it did not know, which was defensible only
/// while E7 emitted one of those two shapes verbatim. Now that E7 emits flags of its own, that
/// rule would be brittle in the one direction that matters: a user who hand-launches
/// <c>ssh -J bastion myhost</c>, or whose profile Bosun later grows another option for, would
/// silently stop appearing in the session list. So rather than matching whole shapes, this walks
/// the argument list the way <c>getopt</c> does -- skipping flags, consuming the argument of any
/// flag known to take one, and taking the first non-flag token as the target.
/// </para>
/// <para>
/// E7's optional reconnect wrapper is a shell loop *around* the ssh invocation; it never changes
/// what <c>ssh.exe</c> itself was launched with (that is the literal command line CIM reports for
/// the <c>ssh.exe</c> process), so it needs no handling here. Feeding the wrapped string in
/// returns <see langword="null"/>, correctly -- its first token is <c>cmd.exe</c>, not <c>ssh</c>.
/// </para>
/// <para>
/// Anything unparseable -- a bare invocation, a flag missing its argument, a non-ssh command line
/// -- returns <see langword="null"/> rather than throwing. Per bs-8dr that is not an error: the
/// caller simply will not correlate it to a host.
/// </para>
/// </remarks>
public static class SshCommandLineParser
{
    /// <summary>
    /// Every <c>ssh</c> option that consumes the following token as its value, from ssh(1). The
    /// list matters because a flag's value must never be mistaken for the target: in
    /// <c>ssh -i key myhost</c> the target is <c>myhost</c>, but a parser unaware that <c>-i</c>
    /// takes a value would answer <c>key</c>.
    /// </summary>
    private static readonly HashSet<string> ValueTakingFlags = new(StringComparer.Ordinal)
    {
        "-B", "-b", "-c", "-D", "-E", "-e", "-F", "-I", "-i", "-J", "-L", "-l",
        "-m", "-O", "-o", "-p", "-Q", "-R", "-S", "-W", "-w",
    };

    /// <summary>
    /// Extracts the target of an <c>ssh.exe</c> command line, or <see langword="null"/> if this is
    /// not a parseable ssh invocation.
    /// </summary>
    public static SshTarget? TryParse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return null;
        }

        var exeName = Path.GetFileName(tokens[0]);
        if (!exeName.Equals("ssh", StringComparison.OrdinalIgnoreCase) &&
            !exeName.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? user = null;
        int? port = null;
        string? target = null;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Length > 1 && token[0] == '-')
            {
                if (!ValueTakingFlags.Contains(token))
                {
                    // A boolean flag (-t, -v, -4, ...). Nothing to consume.
                    continue;
                }

                if (i + 1 >= tokens.Count)
                {
                    // A value-taking flag with nothing after it: malformed, and guessing what was
                    // meant is worse than declining to correlate.
                    return null;
                }

                var value = tokens[++i];
                if (token == "-p"
                    && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
                {
                    port = parsedPort;
                }
                else if (token == "-l")
                {
                    user = value;
                }

                continue;
            }

            if (token.Length == 0)
            {
                continue;
            }

            // First non-flag token is the target. Everything after it is the remote command
            // (e.g. "tmux new -A -s main") and is none of this parser's business.
            target = token;
            break;
        }

        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        // Split on the LAST '@'. ssh treats the final '@' as the user/host separator, so taking
        // the last one keeps a domain-qualified username (DOMAIN\user@host) intact.
        var at = target.LastIndexOf('@');
        if (at >= 0)
        {
            var inlineUser = target[..at];
            target = target[(at + 1)..];
            if (target.Length == 0)
            {
                return null;
            }

            if (inlineUser.Length > 0)
            {
                // An inline user@ beats -l, matching ssh's own precedence.
                user = inlineUser;
            }
        }

        return new SshTarget { Host = target, User = user, Port = port };
    }

    /// <summary>
    /// Splits a Windows-style command line on whitespace, treating a double-quoted span (e.g. a
    /// key path containing spaces) as a single token. Deliberately simple: no backslash escaping,
    /// no support for embedded quotes -- E7 never needs either, and malformed input just needs to
    /// not throw, not to round-trip perfectly.
    /// </summary>
    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
