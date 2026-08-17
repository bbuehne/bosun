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

    public static LaunchContext Detect(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

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
