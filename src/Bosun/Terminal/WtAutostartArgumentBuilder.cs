using Bosun.ClaudeSessions;

namespace Bosun.Terminal;

/// <summary>
/// Pure function from profile arguments (braced GUIDs, see <see cref="TerminalProfileArgument"/>)
/// to ONE <c>wt.exe</c> argument list (bs-e28):
/// <c>-w 0 new-tab -p &lt;profile&gt; ; new-tab -p &lt;profile&gt; ...</c>. Same shape as
/// <see cref="WtRestoreArgumentBuilder"/>: every element is exactly one argument for
/// <c>ProcessStartInfo.ArgumentList</c>, tabs are separated by a bare <c>;</c> argument, and a
/// <c>;</c> inside an entry is escaped to <c>\;</c> so Terminal does not read it as a subcommand
/// separator (inert for GUIDs, which contain no <c>;</c>; kept as defence). The escape is that
/// class's, not a second copy; see its remarks for the source of the rule.
/// </summary>
/// <remarks>Not verified against a real Terminal: no default-suite test may launch <c>wt.exe</c>.</remarks>
public static class WtAutostartArgumentBuilder
{
    private const string Separator = ";";

    /// <exception cref="ArgumentException">The list is empty or an entry is blank. An empty list would
    /// make <c>wt -w 0</c> open a stray default tab, so the caller must check first.</exception>
    public static IReadOnlyList<string> Build(IReadOnlyList<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one profile is required to build a wt.exe autostart command.", nameof(profiles));
        }

        List<string> args = ["-w", "0"];
        for (var i = 0; i < profiles.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(profiles[i]))
            {
                throw new ArgumentException("A profile argument is blank.", nameof(profiles));
            }

            if (i > 0)
            {
                args.Add(Separator);
            }

            args.Add("new-tab");
            args.Add("-p");
            args.Add(WtRestoreArgumentBuilder.EscapeSemicolons(profiles[i]));
        }

        return args;
    }
}
