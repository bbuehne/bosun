using Bosun.Configuration;

namespace Bosun.Terminal;

/// <summary>
/// Maps configured hosts to Terminal profiles (E7b, bs-i0q). Pure function from config to model --
/// no filesystem, no process, no <see cref="IHostConfigStore"/> dependency.
/// </summary>
/// <remarks>
/// <para>
/// <b>The commandline (ADR-013).</b> Uses the host's <see cref="HostConfig.Key"/> -- the TOML key,
/// e.g. <c>example-nas</c> -- never <see cref="HostConfig.DisplayName"/> and never a fully
/// specified <c>user@hostname</c>. A fully specified form would target the host directly and so
/// would not match a <c>Host &lt;alias&gt;</c> block in the user's own <c>ssh_config</c>, silently
/// discarding <c>ProxyJump</c> and everything else configured there.
/// </para>
/// <para>
/// <b>Hosts with <c>mount.mode = "none"</c> still get a profile.</b> Terminal-profile generation
/// reads only <see cref="HostConfig.Key"/>/<see cref="HostConfig.DisplayName"/>/
/// <see cref="HostConfig.Session"/> -- never <see cref="HostConfig.Mount"/> -- so a mount-less jump
/// host is included automatically, with no special-casing needed (ADR-008: the terminal and mount
/// axes are independent).
/// </para>
/// <para>
/// <b>The inner ssh invocation vs. the outer Terminal commandline.</b>
/// <see cref="BuildSshInvocation"/> returns exactly one of E8's two recognised forms
/// (<see cref="SessionMonitor.SshCommandLineParser"/>) regardless of
/// <see cref="SessionConfig.Reconnect"/>. When reconnect is off, that string IS the profile's
/// <c>commandline</c> field verbatim. When reconnect is on, the profile's <c>commandline</c> is a
/// <c>cmd.exe</c> loop that WRAPS this string as the literal child command it repeatedly launches
/// -- see <see cref="CreateProfile"/>'s remarks for why that distinction matters to E8.
/// </para>
/// </remarks>
public static class FragmentProfileGenerator
{
    /// <summary>The fragment "app-name" -- the directory name under
    /// <c>...\Windows Terminal\Fragments\</c> (<c>Fragments\Bosun\bosun.json</c>) and the first hop
    /// of <see cref="TerminalGuid.DeriveProfileGuid"/>.</summary>
    public const string AppName = "Bosun";

    /// <summary>Every profile sets this explicitly (see <see cref="FragmentProfile.StartingDirectory"/>'s
    /// remarks on why relying on Terminal's undocumented default is not safe). There is no
    /// per-host config field for it -- <c>%USERPROFILE%</c> is a fixed, sane default for every
    /// profile Bosun emits.</summary>
    public const string DefaultStartingDirectory = "%USERPROFILE%";

    /// <summary>Fallback tmux session name used only if <see cref="SessionConfig.Tmux"/> is true but
    /// <see cref="SessionConfig.TmuxSession"/> is null/empty. <c>ConfigValidator</c>'s
    /// <c>tmux-requires-session</c> rule (bs-ze7) should have caught this before it ever reaches
    /// here -- this fallback is defence-in-depth only, matching the same pattern already used for
    /// <c>failures_before_unmount</c> and <c>vfs_cache_mode</c>: validation is the real guard, this
    /// is what keeps the generator from throwing or producing a malformed commandline if it is ever
    /// bypassed (e.g. a config loaded before the rule existed).</summary>
    public const string DefaultTmuxSession = "main";

    /// <summary>
    /// Small fixed cap on <see cref="WrapWithReconnectLoop"/>'s retry count (bs-ew1). Necessary
    /// because the loop's own retry condition ("exit code &gt;= 255") cannot distinguish a dropped
    /// connection (exactly 255) from <c>ssh.exe</c> failing to launch at all -- verified against a
    /// real <c>cmd.exe</c>: a not-found command sets <c>errorlevel</c> to 9009, which is also
    /// &gt;= 255, so an uncapped loop retries a permanently-broken invocation forever with nothing
    /// ever surfaced in the tab. This is a maintainer recommendation on an open decision, not a
    /// settled one -- trivially reverted to an unbounded loop if preferred.
    /// </summary>
    public const int MaxReconnectAttempts = 5;

    /// <summary>
    /// Builds the whole fragment document -- one profile per host in <paramref name="hosts"/>,
    /// ordered by display name for a stable, diff-friendly output (Terminal itself does not care
    /// about array order, but a stable order keeps repeated writes from producing unnecessary
    /// churn / noisy git diffs of hand-inspected fragments during development).
    /// </summary>
    public static FragmentDocument CreateDocument(IEnumerable<HostConfig> hosts) => new()
    {
        Profiles = hosts
            .Select(CreateProfile)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray(),
    };

    /// <summary>
    /// Builds a single host's profile.
    /// </summary>
    /// <remarks>
    /// The GUID hash input is <see cref="HostConfig.Key"/>, not <see cref="HostConfig.DisplayName"/>
    /// (ADR-013 Decision 2): the config key is Bosun's stable identity everywhere else in the
    /// system, so renaming <c>display_name</c> must not orphan the user's per-profile Terminal
    /// customisation the way it would if Terminal derived the GUID itself from the profile's
    /// <c>name</c> field.
    /// </remarks>
    public static FragmentProfile CreateProfile(HostConfig host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var sshInvocation = BuildSshInvocation(host);
        var commandLine = host.Session.Reconnect ? WrapWithReconnectLoop(sshInvocation) : sshInvocation;

        return new FragmentProfile
        {
            Name = host.DisplayName,
            Guid = TerminalGuid.DeriveProfileGuid(AppName, host.Key),
            CommandLine = commandLine,
            StartingDirectory = DefaultStartingDirectory,
            ColorScheme = host.Session.ColorScheme,
            TabColor = host.Session.TabColor,
        };
    }

    /// <summary>
    /// The exact command that ends up as <c>ssh.exe</c>'s own argv -- one of E8's two recognised
    /// forms, always, whether or not <see cref="SessionConfig.Reconnect"/> wraps it:
    /// <code>
    /// ssh &lt;config-key&gt;
    /// ssh -t &lt;config-key&gt; tmux new -A -s &lt;session&gt;
    /// </code>
    /// Exposed separately from <see cref="CreateProfile"/> so callers -- including the E7/E8
    /// contract test -- can verify what <c>ssh.exe</c> itself will be launched with, independent of
    /// whatever process launches it (Terminal directly, or <c>cmd.exe</c> via the reconnect loop).
    /// </summary>
    public static string BuildSshInvocation(HostConfig host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.Session.Tmux)
        {
            return $"ssh {host.Key}";
        }

        var session = string.IsNullOrWhiteSpace(host.Session.TmuxSession) ? DefaultTmuxSession : host.Session.TmuxSession;
        return $"ssh -t {host.Key} tmux new -A -s {session}";
    }

    /// <summary>
    /// Wraps <paramref name="sshInvocation"/> in a <c>cmd.exe</c> loop that retries on exit code 255
    /// (dropped connection) and stops on anything else, including 0 (user typed <c>exit</c>), up to
    /// <see cref="MaxReconnectAttempts"/> tries -- docs/CONFIG-SCHEMA.md's <c>session.reconnect</c>
    /// row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this doesn't change what E8 sees.</b> Terminal's <c>commandline</c> field launches a
    /// process directly (no shell interprets it) -- so a retry loop needs an actual shell to host
    /// it, which is why the OUTER process here is <c>cmd.exe</c>, not <c>ssh.exe</c>. But the INNER
    /// command the loop repeatedly launches is <paramref name="sshInvocation"/> verbatim, so the
    /// <c>ssh.exe</c> CHILD process <c>cmd.exe</c> spawns each iteration has exactly the same
    /// command line E8's <see cref="SessionMonitor.SshCommandLineParser"/> already recognises --
    /// <see cref="SessionMonitor.SshCommandLineParser"/> reads a live <c>ssh.exe</c> process's own
    /// argv via CIM, not Terminal's top-level <c>commandline</c> field, so it never sees the
    /// <c>cmd.exe</c> wrapper at all. What it WOULD break is feeding the full wrapped string (this
    /// method's return value) into the parser directly, as one might naively do for a "does E7's
    /// output round-trip through E8" test -- that returns null, correctly, because the wrapped
    /// string's own first token is <c>cmd.exe</c>, not <c>ssh</c>. The contract test in
    /// <c>tests/Bosun.Tests/Terminal</c> therefore feeds <see cref="BuildSshInvocation"/>'s result,
    /// not this method's, into the parser.
    /// </para>
    /// <para>
    /// <b>Why bounded (bs-ew1).</b> The original unbounded loop (<c>for /l %n in (1,0,2)</c>, a step
    /// of 0 so the counter never advances and the loop never ends on its own) relied on <c>if not
    /// errorlevel 255 exit /b</c> to stop it -- but that comparison is "&gt;=", not "==" (see below),
    /// so it stops on ANY exit code &lt; 255, not specifically on 0 or specifically on 255. If
    /// <c>ssh.exe</c> cannot be launched at all (not on PATH, typo'd config key), verified against a
    /// real <c>cmd.exe</c> shows <c>errorlevel</c> becomes 9009 -- Windows' own "command not
    /// recognised" code -- which is itself &gt;= 255, so the loop treats "ssh does not exist" exactly
    /// like "connection dropped" and retries it forever, with nothing ever surfaced in the tab beyond
    /// cmd.exe's own one-line "is not recognized" message repeating silently off the top of
    /// scrollback. Capping at <see cref="MaxReconnectAttempts"/> turns that into a bounded, visible
    /// failure: after the cap, the tab prints the last exit code and exits, instead of scrolling
    /// forever.
    /// </para>
    /// <para>
    /// <c>if not errorlevel 255 exit /b !errorlevel!</c> relies on ssh's exit code never exceeding
    /// 255 (the standard POSIX exit-code range, which <c>cmd.exe</c>'s <c>errorlevel</c> comparison
    /// treats as "&gt;=" rather than "=="): since nothing in that range can exceed 255, this is
    /// equivalent to "exit code was less than 255", i.e. anything other than the dropped-connection
    /// code, matching the exact two-code contract in docs/CONFIG-SCHEMA.md.
    /// </para>
    /// <para>
    /// <b>Two non-obvious <c>cmd.exe</c> quirks this depends on, both verified against a real
    /// <c>cmd.exe</c> in a throwaway script (never committed, never touching this repo).</b>
    /// (1) When this whole string is launched via <c>cmd.exe /d /c "..."</c> -- a single command
    /// line, not a <c>.bat</c> file -- plain <c>%errorlevel%</c> substitution happens ONCE, at parse
    /// time, for the entire line, before anything in it has run; it is therefore always stale (here,
    /// always 0). Delayed expansion (<c>/v:on</c>, and <c>!errorlevel!</c> instead of
    /// <c>%errorlevel%</c>) is required to read the real, current value -- <c>if errorlevel N</c>
    /// itself is unaffected either way, since that is a built-in runtime test, not a variable
    /// substitution. (2) An un-parenthesized <c>if condition action</c> followed by <c>&amp;
    /// nextStatement</c>, in this same single-line context, does NOT let <c>nextStatement</c> run
    /// unconditionally the way it would inside a <c>.bat</c> file -- when the condition is false, the
    /// <c>&amp;</c>-chained statement after it is swallowed along with the untaken branch. Every
    /// <c>if</c> below is therefore wrapped in its own outer parens (<c>(if ... (...))</c>) so it
    /// cannot absorb what follows it.
    /// </para>
    /// </remarks>
    private static string WrapWithReconnectLoop(string sshInvocation) =>
        $"cmd.exe /d /v:on /c \"for /l %n in (1,1,{MaxReconnectAttempts}) do ({sshInvocation} & " +
        "(if not errorlevel 255 (exit /b !errorlevel!)) & " +
        $"(if %n=={MaxReconnectAttempts} (echo Bosun: giving up after {MaxReconnectAttempts} attempts -- last exit code !errorlevel! & exit /b !errorlevel!)))\"";
}
