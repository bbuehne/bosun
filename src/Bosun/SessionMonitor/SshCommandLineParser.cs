using System.IO;
using System.Text;

namespace Bosun.SessionMonitor;

/// <summary>
/// Pure parsing of an <c>ssh.exe</c> command line into the target host token (bs-8dr). No
/// process, CIM, or config access -- this is the seam that can be unit-tested exhaustively
/// without touching a real OS, per docs/ARCHITECTURE.md §3.
/// </summary>
/// <remarks>
/// Recognises exactly the two forms E7 emits (docs/ARCHITECTURE.md §3):
/// <code>
/// ssh &lt;host&gt;
/// ssh -t &lt;host&gt; tmux new -A -s &lt;session&gt;
/// </code>
/// <c>&lt;host&gt;</c> is the target host's config key (<see cref="Configuration.HostConfig.Key"/>),
/// not <c>display_name</c> -- see bs-08g. E7's optional reconnect wrapper is a shell loop
/// *around* the ssh invocation; it never changes what <c>ssh.exe</c> itself was launched with
/// (that's the literal command line CIM reports for the <c>ssh.exe</c> process), so it needs no
/// special handling here.
///
/// Anything else -- a hand-launched <c>ssh</c> with extra flags, a target that isn't a
/// configured key, a bare or malformed invocation -- returns <see langword="null"/> rather than
/// throwing. Per bs-8dr, that is not an error: the caller simply won't correlate it to a host.
/// </remarks>
public static class SshCommandLineParser
{
    /// <summary>
    /// Extracts the target host token from an <c>ssh.exe</c> command line, or
    /// <see langword="null"/> if the command line doesn't match a recognised form.
    /// </summary>
    public static string? TryParseTargetHost(string? commandLine)
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

        var index = 1;
        if (index < tokens.Count && tokens[index] == "-t")
        {
            index++;
        }

        if (index >= tokens.Count)
        {
            return null;
        }

        var host = tokens[index];
        if (host.Length == 0 || host[0] == '-')
        {
            // Either an unrecognised flag (a hand-launched "ssh -p 2200 host") or an empty
            // token -- neither is one of E7's two forms. Not an error; just not a match.
            return null;
        }

        return host;
    }

    /// <summary>
    /// Splits a Windows-style command line on whitespace, treating a double-quoted span (e.g. a
    /// quoted executable path containing spaces) as a single token. Deliberately simple: no
    /// backslash escaping, no support for embedded quotes -- E7 never needs either, and
    /// malformed input just needs to not throw, not to round-trip perfectly.
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
