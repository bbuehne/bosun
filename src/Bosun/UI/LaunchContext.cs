namespace Bosun.UI;

/// <summary>
/// Which way this process was started (ADR-018 Decision 2 / bs-ww9.3). Governs whether
/// <see cref="MainWindowController"/> shows the window on startup.
/// </summary>
public enum LaunchContext
{
    /// <summary>Started by the user double-clicking the exe or a shortcut. Show the window.</summary>
    Manual,

    /// <summary>
    /// Started from <c>shell:startup</c> at login. Start to tray with no window -- ADR-018's
    /// reasoning, carried over verbatim from ADR-012: "the user is walking away, windows are
    /// fighting for focus."
    /// </summary>
    Autostart,

    /// <summary>
    /// Started by Bosun's own watchdog replacing a stalled instance (bs-6to, bs-aoz). Starts to tray
    /// with no window, like <see cref="Autostart"/> and for the same reason: the restart happens when
    /// nobody asked for it, often with the user away, and a window appearing at 3 a.m. is a surprise.
    /// What happened is reported in the health banner instead (<c>watchdog.restarted</c>).
    /// </summary>
    WatchdogRestart,

    /// <summary>
    /// Started by Bosun on the user's own "Restart Bosun" request (bs-aoz). Shows the window: the
    /// user just asked for this, and a restart that leaves nothing on screen looks like a quit.
    /// </summary>
    UserRestart,
}

/// <summary>What each <see cref="LaunchContext"/> does about the window.</summary>
public static class LaunchContextExtensions
{
    /// <summary>True when the main window should be shown at startup (ADR-018 rule 2): the user launched
    /// Bosun by hand, or asked it to restart. False when it starts quietly to the tray.</summary>
    public static bool ShowsWindowAtStartup(this LaunchContext context) =>
        context is LaunchContext.Manual or LaunchContext.UserRestart;
}

/// <summary>
/// Detects <see cref="LaunchContext"/> from the process command line. Pure and side-effect free
/// so it is unit-testable without a live WPF <see cref="System.Windows.Application"/>.
/// </summary>
/// <remarks>
/// <b>The flag is <c>--autostart</c>.</b> This is the one and only mechanism ADR-018 rule 2 keys
/// off, and it is the flag E10's autostart registration (a shortcut under <c>shell:startup</c>
/// pointing at <c>Bosun.exe --autostart</c>) must inherit rather than inventing a second one -- see
/// the bs-ww9.3 brief and the amendment recorded under ADR-018 in docs/DECISIONS.md. Case-insensitive
/// and tolerant of being anywhere in <c>args</c> (not required to be the first/only argument), so a
/// future second flag (e.g. a config-path override) can coexist without this detector caring about
/// order.
/// </remarks>
public static class LaunchContextDetector
{
    public const string AutostartArgument = "--autostart";

    /// <remarks>
    /// Precedence (bs-aoz): a restart flag outranks <c>--autostart</c>, because a restarted Bosun
    /// inherits the original's arguments and the restart is the more recent fact about how it started.
    /// The user's restart flag outranks the watchdog's. <see cref="Watchdog.RestartHandoffArguments"/>
    /// already strips <c>--autostart</c> from a user restart, so this ordering is the second line of
    /// defence, not the first.
    /// </remarks>
    public static LaunchContext Detect(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (Watchdog.RestartHandoffArguments.TryGetHandoff(args, out var kind, out _))
        {
            return kind == Watchdog.RestartKind.Manual ? LaunchContext.UserRestart : LaunchContext.WatchdogRestart;
        }

        foreach (var arg in args)
        {
            if (string.Equals(arg, AutostartArgument, StringComparison.OrdinalIgnoreCase))
            {
                return LaunchContext.Autostart;
            }
        }

        return LaunchContext.Manual;
    }
}
