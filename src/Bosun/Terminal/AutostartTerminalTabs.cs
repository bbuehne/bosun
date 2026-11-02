using Bosun.Configuration;
using Microsoft.Extensions.Logging;

namespace Bosun.Terminal;

/// <summary>
/// Opens one Windows Terminal tab per host that has <c>session.autostart = true</c>, in a single
/// <c>wt -w 0</c> call (bs-e28). WHETHER to open them is not decided here: that is the caller's
/// launch-context and fragment-written gate (<see cref="Bosun.Hosting.StartupOrchestrator"/>).
/// </summary>
/// <remarks>
/// Autostart is about terminal sessions and is independent of mounting (ADR-008's three axes), so a
/// <c>mode = "none"</c> host gets its tab like any other. There is no per-host <c>enabled</c> flag
/// in <see cref="HostConfig"/>; every configured host is eligible. The tab opens the profile named
/// by <see cref="HostConfig.DisplayName"/>, which is the <c>name</c> the fragment generator writes
/// (<see cref="FragmentProfileGenerator"/>). Never throws: a failure to build or launch is logged.
/// </remarks>
public sealed class AutostartTerminalTabs(ITerminalTabLauncher launcher, ILogger logger)
{
    /// <summary>The profile names to open, in a stable order (host key, ordinal).</summary>
    public static IReadOnlyList<string> SelectProfileNames(BosunConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Hosts.Values
            .Where(host => host.Session.Autostart)
            .OrderBy(host => host.Key, StringComparer.Ordinal)
            .Select(host => host.DisplayName)
            .ToList();
    }

    /// <returns>The number of tabs requested; 0 when nothing was launched.</returns>
    public int Open(BosunConfig config)
    {
        try
        {
            var names = SelectProfileNames(config);
            if (names.Count == 0)
            {
                logger.LogDebug("No host has session.autostart = true; no Terminal tabs to open");
                return 0;
            }

            launcher.OpenTabs(WtAutostartArgumentBuilder.Build(names));
            logger.LogInformation("Requested {Count} autostart Terminal tab(s): {Profiles}", names.Count, string.Join(", ", names));
            return names.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not open the autostart Terminal tabs; continuing without them");
            return 0;
        }
    }
}
