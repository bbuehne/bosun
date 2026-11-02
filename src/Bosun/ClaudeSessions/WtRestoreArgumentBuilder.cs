using System.IO;

namespace Bosun.ClaudeSessions;

/// <summary>
/// Pure function from restore tabs to ONE <c>wt.exe</c> argument list (ADR-021 Decision 6):
/// <c>-w 0 new-tab -p &lt;profile&gt; -d &lt;cwd&gt; --title &lt;title&gt; &lt;tab root&gt; &lt;keep-open&gt;
/// &lt;claude&gt; --resume &lt;id&gt; ; new-tab ...</c>. The result is meant for
/// <c>ProcessStartInfo.ArgumentList</c>, so every element is exactly one argument: .NET does the
/// Windows command-line quoting (spaces, trailing backslashes), and this class only handles what
/// .NET cannot know about, which is how <c>wt.exe</c> itself treats <c>;</c>.
/// </summary>
/// <remarks>
/// <para><b>Semicolons.</b> <c>wt.exe</c> uses <c>;</c> to separate subcommands, and it looks for
/// it inside every argument, not just as a standalone token. From Windows Terminal's source
/// (<c>src/cascadia/TerminalApp/AppCommandlineArgs.cpp</c>, <c>_commandDelimiterRegex</c> =
/// <c>^;|[^\\];</c>, with the comment "If the user would like to provide ';' in the text of the
/// commandline, they can escape it as \;") and <c>Commandline.cpp</c> (<c>Commandline::AddArg</c>
/// replaces each <c>\;</c> with <c>;</c> after splitting): a <c>;</c> that is at the start of an
/// argument or preceded by any character other than <c>\</c> ends the command. So we replace
/// every <c>;</c> inside a value with <c>\;</c>. That is also correct when the value already had a
/// backslash before the <c>;</c> (<c>a\;b</c> becomes <c>a\\;b</c>; the <c>;</c> is preceded by
/// <c>\</c>, so it does not split, and de-escaping removes exactly one backslash, restoring
/// <c>a\;b</c>). Microsoft's user documentation
/// (https://learn.microsoft.com/windows/terminal/command-line-arguments) describes the bare
/// <c>;</c> separator and the PowerShell backtick escape; the <c>\;</c> literal form is only
/// documented in the source, so it is cited here from there. The separator between tabs is a
/// bare <c>;</c> argument, deliberately NOT escaped.</para>
/// <para><b>Not verified against a real Terminal.</b> This builder is unit-tested only; no test
/// may launch <c>wt.exe</c> (ADR-021). Known limit: a title or profile id that begins with
/// <c>-</c> could be read by Terminal's option parser as a flag.</para>
/// </remarks>
public static class WtRestoreArgumentBuilder
{
    private const string Separator = ";";

    /// <summary>
    /// Builds the argument list for one <c>wt.exe</c> invocation that opens one tab per entry in
    /// <paramref name="tabs"/>, in order. An empty list throws <see cref="ArgumentException"/>
    /// rather than yielding an empty list: <c>wt -w 0</c> with nothing after it opens a stray
    /// default tab, and a caller that asks to restore nothing has a bug it should hear about.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty, a session id is empty or contains
    /// whitespace or <c>;</c>, or a required path (cwd, Claude image) is empty.</exception>
    public static IReadOnlyList<string> Build(IReadOnlyList<ClaudeRestoreTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0)
        {
            throw new ArgumentException("At least one tab is required to build a wt.exe restore command.", nameof(tabs));
        }

        // One leading "-w 0" (most recently used window, as in ADR-013 / ADR-021 Decision 6);
        // the "new-tab" subcommands that follow are separated by a bare ";".
        List<string> args = ["-w", "0"];
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i > 0)
            {
                args.Add(Separator);
            }

            AppendTab(args, tabs[i]);
        }

        return args;
    }

    private static void AppendTab(List<string> args, ClaudeRestoreTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ValidateSessionId(tab.SessionId);
        if (string.IsNullOrWhiteSpace(tab.Cwd))
        {
            throw new ArgumentException($"Session '{tab.SessionId}' has no working directory.", nameof(tab));
        }

        if (string.IsNullOrWhiteSpace(tab.ClaudeImagePath))
        {
            throw new ArgumentException($"Session '{tab.SessionId}' has no Claude Code image path.", nameof(tab));
        }

        args.Add("new-tab");

        // A missing profile id omits -p, so Terminal opens its default profile rather than
        // failing the whole restore over one record that lacks WT_PROFILE_ID.
        if (!string.IsNullOrWhiteSpace(tab.ProfileId))
        {
            args.Add("-p");
            args.Add(EscapeSemicolons(tab.ProfileId));
        }

        args.Add("-d");
        args.Add(EscapeSemicolons(tab.Cwd));
        args.Add("--title");
        args.Add(EscapeSemicolons(string.IsNullOrWhiteSpace(tab.Title) ? FolderName(tab.Cwd) : tab.Title));

        AppendCommand(args, tab);
    }

    private static void AppendCommand(List<string> args, ClaudeRestoreTab tab)
    {
        var tabRoot = tab.TabRootImagePath;
        var rootIsClaude = !string.IsNullOrWhiteSpace(tabRoot)
            && string.Equals(tabRoot, tab.ClaudeImagePath, StringComparison.OrdinalIgnoreCase);

        // Decision 6: Claude Code ran inside the tab root, so /exit should leave that shell's
        // prompt behind. A root that IS Claude Code, or one we do not know how to keep open, gets
        // no wrapper: wt.exe launches the recorded Claude image directly (it cannot launch the npm
        // claude.cmd shim by name), and the tab closes when Claude Code exits.
        if (!rootIsClaude && !string.IsNullOrWhiteSpace(tabRoot))
        {
            string[]? keepOpen = Path.GetFileName(tabRoot).ToLowerInvariant() switch
            {
                "pwsh.exe" or "powershell.exe" => ["-NoExit", "-Command"],
                "cmd.exe" => ["/k"],
                _ => null,
            };

            if (keepOpen is not null)
            {
                args.Add(EscapeSemicolons(tabRoot));
                args.AddRange(keepOpen);
                args.Add("claude"); // resolved by the shell, so the npm shim works here
                args.Add("--resume");
                args.Add(tab.SessionId);
                return;
            }
        }

        args.Add(EscapeSemicolons(tab.ClaudeImagePath));
        args.Add("--resume");
        args.Add(tab.SessionId);
    }

    private static void ValidateSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new ArgumentException("Session id is empty.", nameof(sessionId));
        }

        // Session ids are GUID-like. Anything with whitespace or ';' would either split into two
        // arguments inside a -Command string or become a wt.exe subcommand separator.
        foreach (var c in sessionId)
        {
            if (char.IsWhiteSpace(c) || c == ';')
            {
                throw new ArgumentException($"Session id '{sessionId}' contains whitespace or ';'.", nameof(sessionId));
            }
        }
    }

    /// <summary>
    /// The cwd's folder name: the last path segment ignoring trailing separators. A drive root has
    /// no segment, so <c>C:\</c> yields <c>C:</c>.
    /// </summary>
    private static string FolderName(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return cwd; // "\" or "/" alone
        }

        // Not Path.GetFileName: it returns "" for a UNC share root like \\server\share.
        return trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    internal static string EscapeSemicolons(string value) => value.Replace(";", "\\;", StringComparison.Ordinal);
}
