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
}
