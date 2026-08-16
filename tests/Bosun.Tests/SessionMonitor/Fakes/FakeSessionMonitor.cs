using Bosun.SessionMonitor;

namespace Bosun.Tests.SessionMonitor.Fakes;

/// <summary>
/// Fake <see cref="ISessionMonitor"/> (bs-gme): the seam every other epic's tests use to stand
/// in for real ssh.exe enumeration and TCP-table interop. E9's tray/status tests should reach
/// for this rather than composing the real CIM/P-Invoke pipeline.
/// </summary>
internal sealed class FakeSessionMonitor : ISessionMonitor
{
    private IReadOnlyList<SshSession> _sessions = [];

    public void SetSessions(IReadOnlyList<SshSession> sessions) => _sessions = sessions;

    public IReadOnlyList<SshSession> GetActiveSessions() => _sessions;
}
