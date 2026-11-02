namespace Bosun.Terminal;

/// <summary>
/// Starts ONE <c>wt.exe</c> process with an argument list built by this layer. A plain "start a
/// process" seam: it knows nothing about hosts, profiles or config, and it never touches a mount,
/// a drive letter or <c>IRcloneClient</c>. It exists in <c>Bosun.Terminal</c> so that
/// <see cref="Bosun.Hosting.StartupOrchestrator"/> can open the autostart tabs (bs-e28) without
/// depending on the UI layer; the real implementation is <c>Win32ExternalLauncher</c>.
/// </summary>
public interface ITerminalTabLauncher
{
    /// <summary>
    /// Runs <c>wt.exe</c> with <paramref name="wtArguments"/> exactly as given, as one
    /// <c>ProcessStartInfo.ArgumentList</c>. Best-effort: an implementation logs a launch failure
    /// and does not throw it.
    /// </summary>
    void OpenTabs(IReadOnlyList<string> wtArguments);
}
