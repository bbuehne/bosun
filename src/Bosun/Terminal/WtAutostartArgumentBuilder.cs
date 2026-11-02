using Bosun.ClaudeSessions;

namespace Bosun.Terminal;

/// <summary>
/// Pure function from profile names to ONE <c>wt.exe</c> argument list (bs-e28):
/// <c>-w 0 new-tab -p &lt;profile&gt; ; new-tab -p &lt;profile&gt; ...</c>. Same shape as
/// <see cref="WtRestoreArgumentBuilder"/>: every element is exactly one argument for
/// <c>ProcessStartInfo.ArgumentList</c>, tabs are separated by a bare <c>;</c> argument, and a
/// <c>;</c> inside a name is escaped to <c>\;</c> so Terminal does not read it as a subcommand
/// separator. The escape is that class's, not a second copy; see its remarks for the source of the
/// rule.
/// </summary>
/// <remarks>Not verified against a real Terminal: no default-suite test may launch <c>wt.exe</c>.</remarks>
public static class WtAutostartArgumentBuilder
{
    private const string Separator = ";";

    /// <exception cref="ArgumentException">The list is empty or a name is blank. An empty list would
    /// make <c>wt -w 0</c> open a stray default tab, so the caller must check first.</exception>
    public static IReadOnlyList<string> Build(IReadOnlyList<string> profileNames)
    {
        ArgumentNullException.ThrowIfNull(profileNames);
        if (profileNames.Count == 0)
        {
            throw new ArgumentException("At least one profile is required to build a wt.exe autostart command.", nameof(profileNames));
        }

        List<string> args = ["-w", "0"];
        for (var i = 0; i < profileNames.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(profileNames[i]))
            {
                throw new ArgumentException("A profile name is blank.", nameof(profileNames));
            }

            if (i > 0)
            {
                args.Add(Separator);
            }

            args.Add("new-tab");
            args.Add("-p");
            args.Add(WtRestoreArgumentBuilder.EscapeSemicolons(profileNames[i]));
        }

        return args;
    }
}
