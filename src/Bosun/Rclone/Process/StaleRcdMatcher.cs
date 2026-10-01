using System.IO;
using System.Text;

namespace Bosun.Rclone.Process;

/// <summary>What the guard knows about a process holding the rc port. Every field is nullable
/// because a field the OS would not give us (access denied, process already gone) must read as
/// "unknown", and unknown can never qualify a process for being killed.</summary>
public sealed record ProcessDescription
{
    public required int ProcessId { get; init; }

    /// <summary>Image file name, e.g. <c>rclone.exe</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Full image path.</summary>
    public string? ImagePath { get; init; }

    public string? CommandLine { get; init; }

    /// <summary>Owning account as <c>DOMAIN\user</c>.</summary>
    public string? Owner { get; init; }

    public DateTimeOffset? StartTime { get; init; }
}

/// <summary>
/// Decides whether a port holder is "a stale rcd that only Bosun would have launched"
/// (ADR-020 §2). Pure functions, so the whole decision table is unit-tested with no process.
/// </summary>
/// <remarks>
/// The rule is deliberately narrow, because the cost of a false positive is killing somebody's
/// process: image <c>rclone.exe</c>, same user, and a command line that is EXACTLY
/// <c>rcd --rc-addr 127.0.0.1:&lt;port&gt; --config &lt;our path&gt;</c>. It tolerates argument
/// order, the <c>--flag=value</c> spelling, quoting, and path case/separators; it does NOT
/// tolerate any extra argument (a hand-started <c>rclone rcd --rc-web-gui</c> is not ours), a
/// duplicate flag, a different port or config, a relative config path, or any missing field.
/// </remarks>
public static class StaleRcdMatcher
{
    public static bool IsBosunsOwnRcd(ProcessDescription process, int port, string configPath, string currentUser)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (process.Name is null || process.ImagePath is null || process.CommandLine is null ||
            process.Owner is null || process.StartTime is null)
        {
            return false;
        }

        if (!string.Equals(process.Name, "rclone.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(process.Owner, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CommandLineMatches(process.CommandLine, port, configPath);
    }

    public static bool CommandLineMatches(string commandLine, int port, string configPath)
    {
        var args = WindowsCommandLine.Split(commandLine);
        if (args.Count < 2)
        {
            return false;
        }

        // args[0] is the executable. Its file name must itself be rclone: a renamed binary with
        // a plausible tail is not ours.
        var exeName = Path.GetFileName(args[0].Replace('/', '\\'));
        if (!string.Equals(exeName, "rclone.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(exeName, "rclone", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sawRcd = false;
        string? rcAddr = null;
        string? config = null;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "rcd", StringComparison.Ordinal))
            {
                if (sawRcd)
                {
                    return false;
                }

                sawRcd = true;
                continue;
            }

            var consumed = TryTakeFlag(args, ref i, "--rc-addr", ref rcAddr, out var bad);
            if (bad)
            {
                return false;
            }

            if (consumed)
            {
                continue;
            }

            consumed = TryTakeFlag(args, ref i, "--config", ref config, out bad);
            if (bad)
            {
                return false;
            }

            if (!consumed)
            {
                // Not one of the three things Bosun passes: not an exact match.
                return false;
            }
        }

        if (!sawRcd || rcAddr is null || config is null)
        {
            return false;
        }

        if (!string.Equals(rcAddr, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SamePath(config, configPath);
    }

    /// <summary>Consumes <paramref name="name"/> (as <c>name value</c> or <c>name=value</c>) at
    /// <paramref name="index"/>. Returns <see langword="false"/> when the argument is a
    /// different flag; sets <paramref name="bad"/> when it IS this flag but malformed or
    /// duplicated.</summary>
    private static bool TryTakeFlag(
        IReadOnlyList<string> args, ref int index, string name, ref string? target, out bool bad)
    {
        bad = false;
        var arg = args[index];

        string value;
        if (string.Equals(arg, name, StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                bad = true;
                return true;
            }

            value = args[++index];
        }
        else if (arg.StartsWith(name + "=", StringComparison.Ordinal))
        {
            value = arg[(name.Length + 1)..];
        }
        else
        {
            return false;
        }

        if (target is not null || value.Length == 0)
        {
            bad = true;
            return true;
        }

        target = value;
        return true;
    }

    private static bool SamePath(string a, string b)
    {
        // A relative path in the other process's command line is relative to ITS working
        // directory, which we cannot know, so it can never match.
        if (!Path.IsPathRooted(a) || !Path.IsPathRooted(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// Splits a Windows command line the way <c>CommandLineToArgvW</c> / the MSVCRT does: whitespace
/// separates arguments, double quotes group, <c>2n</c> backslashes before a quote become
/// <c>n</c> backslashes and toggle quoting, <c>2n+1</c> make <c>n</c> backslashes and a literal
/// quote, and the program name (first token) takes no escape processing.
/// </summary>
public static class WindowsCommandLine
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var args = new List<string>();
        var i = 0;
        var n = commandLine.Length;

        // Program name: quoted up to the next quote, otherwise up to whitespace. No escapes.
        SkipWhitespace(commandLine, ref i);
        if (i >= n)
        {
            return args;
        }

        var first = new StringBuilder();
        if (commandLine[i] == '"')
        {
            i++;
            while (i < n && commandLine[i] != '"')
            {
                first.Append(commandLine[i++]);
            }

            if (i < n)
            {
                i++; // closing quote
            }
        }
        else
        {
            while (i < n && !char.IsWhiteSpace(commandLine[i]))
            {
                first.Append(commandLine[i++]);
            }
        }

        args.Add(first.ToString());

        while (true)
        {
            SkipWhitespace(commandLine, ref i);
            if (i >= n)
            {
                break;
            }

            var current = new StringBuilder();
            var inQuotes = false;
            while (i < n)
            {
                var c = commandLine[i];
                if (c == '\\')
                {
                    var backslashes = 0;
                    while (i < n && commandLine[i] == '\\')
                    {
                        backslashes++;
                        i++;
                    }

                    if (i < n && commandLine[i] == '"')
                    {
                        current.Append('\\', backslashes / 2);
                        if (backslashes % 2 == 1)
                        {
                            current.Append('"');
                            i++;
                        }
                        // Even count: the quote is processed by the next loop pass as a toggle.
                    }
                    else
                    {
                        current.Append('\\', backslashes);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (inQuotes && i + 1 < n && commandLine[i + 1] == '"')
                    {
                        current.Append('"'); // "" inside quotes is a literal quote
                        i += 2;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    break;
                }

                current.Append(c);
                i++;
            }

            args.Add(current.ToString());
        }

        return args;
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
    }
}
