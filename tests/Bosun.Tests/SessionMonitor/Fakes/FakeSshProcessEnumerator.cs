using Bosun.SessionMonitor;

namespace Bosun.Tests.SessionMonitor.Fakes;

/// <summary>Fake <see cref="ISshProcessEnumerator"/> so <see cref="SshSessionMonitor"/>'s
/// correlation logic can be tested without touching a real process or CIM.</summary>
internal sealed class FakeSshProcessEnumerator : ISshProcessEnumerator
{
    private IReadOnlyList<SshProcessInfo> _processes = [];
    private Exception? _throws;

    public void SetProcesses(IReadOnlyList<SshProcessInfo> processes) => _processes = processes;

    /// <summary>Makes the next (and every subsequent) call to <see cref="Enumerate"/> throw
    /// <paramref name="exception"/> -- simulates CIM being completely unavailable.</summary>
    public void ThrowOnEnumerate(Exception exception) => _throws = exception;

    public IReadOnlyList<SshProcessInfo> Enumerate()
    {
        if (_throws is not null)
        {
            throw _throws;
        }

        return _processes;
    }
}
