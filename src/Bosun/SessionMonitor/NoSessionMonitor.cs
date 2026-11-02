namespace Bosun.SessionMonitor;

/// <summary>
/// An <see cref="ISessionMonitor"/> that sees no sessions (bs-4js). The UI's fallback when the real
/// monitor cannot be built -- it correlates <c>ssh.exe</c> processes against the host config, so it
/// fails with the config store. Session counts are cosmetic on a host row; their absence must not
/// cost the user the tray icon.
/// </summary>
public sealed class NoSessionMonitor : ISessionMonitor
{
    public IReadOnlyList<SshSession> GetActiveSessions() => [];
}
