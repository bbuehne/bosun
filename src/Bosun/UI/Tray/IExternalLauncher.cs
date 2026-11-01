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
    /// Opens a new Windows Terminal tab for <paramref name="hostKey"/>, using the exact form
    /// CLAUDE.md's definition-of-done and ADR-013 specify: <c>wt -w 0 new-tab -p "&lt;host&gt;"</c>,
    /// where <c>&lt;host&gt;</c> is the host's stable config key (ADR-013 Decision 1) -- the same
    /// identity the Terminal fragment writer (E7) used to name the profile, so this always opens
    /// the profile Bosun generated for that host, never a guess built from display name or hostname.
    /// </summary>
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
