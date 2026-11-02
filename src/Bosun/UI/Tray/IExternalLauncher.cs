namespace Bosun.UI.Tray;

/// <summary>
/// Launches processes OUTSIDE the mount/terminal-profile system on the user's behalf: Windows
/// Terminal for a host, and Explorer at a mounted drive. Deliberately separate from
/// <c>IMountSupervisor</c> and <c>IRcloneClient</c> -- this never touches a mount, a drive letter's
/// contents, or rc endpoints; it just starts an ordinary Win32 process, exactly like a user
/// double-clicking a shortcut would.
/// </summary>
public interface IExternalLauncher
{
    /// <summary>
    /// Opens a new Windows Terminal tab for the host with config key <paramref name="hostKey"/>:
    /// <c>wt -w 0 new-tab -p "{guid}"</c>, where <c>{guid}</c> is the braced profile GUID the
    /// fragment writer derived from that key (<c>TerminalProfileArgument</c>, bs-4ic).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key is NOT the profile's name: the fragment names each profile after the host's
    /// <c>display_name</c> and identifies it by a GUID derived from the key (ADR-013 Decision 2).
    /// Passing the key to <c>-p</c> matched nothing whenever the two differed, and passing the
    /// display name would break on rename. The GUID is the only stable handle.
    /// </para>
    /// <para>
    /// Evidence that <c>-p</c> accepts a GUID: Microsoft's command-line page documents only names;
    /// learn.microsoft.com/windows/terminal/customize-settings/actions says the <c>newTab</c>
    /// action's <c>profile</c> takes a "name or GUID"; and the maintainer verified the braced GUID
    /// with <c>wt -p</c> on the real machine on 2026-11-02.
    /// </para>
    /// </remarks>
    void OpenTerminal(string hostKey);

    /// <summary>Opens an Explorer window rooted at <paramref name="drive"/> (e.g. <c>"P:\\"</c>).</summary>
    void OpenInExplorer(string drive);

    /// <summary>
    /// Runs ONE <c>wt.exe</c> invocation with <paramref name="wtArguments"/> exactly as given, to
    /// reopen Claude Code sessions (ADR-021 Decision 6). The arguments are built by
    /// <c>WtRestoreArgumentBuilder</c>, which the caller invokes; taking the finished argument list
    /// (not the tabs) keeps this interface a plain "start a process" seam with no dependency on
    /// session types, and lets the caller (E13g) drop an individual bad record before the builder
    /// sees it instead of losing the whole batch to one <see cref="ArgumentException"/>. Like
    /// every method here it is best-effort and never throws on launch failure; whether a tab
    /// actually opened is confirmed by the session service, not by this call.
    /// </summary>
    void RestoreClaudeSessions(IReadOnlyList<string> wtArguments);
}
