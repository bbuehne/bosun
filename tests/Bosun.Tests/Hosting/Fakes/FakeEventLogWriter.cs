using Bosun.Hosting;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// In-memory <see cref="IEventLogWriter"/> so <see cref="BootstrapDiagnosticSink"/> can be tested
/// without ever touching the real Windows Event Log (CLAUDE.md worktree-safety rules). Can be
/// configured to throw, matching the realistic case where the "Bosun" event source is not
/// registered and the process is not elevated -- see <see cref="Win32EventLogWriter"/>'s doc
/// comment.
/// </summary>
public sealed class FakeEventLogWriter : IEventLogWriter
{
    private readonly Exception? _throwOnWrite;

    public FakeEventLogWriter(Exception? throwOnWrite = null)
    {
        _throwOnWrite = throwOnWrite;
    }

    public int CallCount { get; private set; }
    public string? LastSource { get; private set; }
    public string? LastMessage { get; private set; }

    public void Write(string source, string message)
    {
        CallCount++;
        LastSource = source;
        LastMessage = message;

        if (_throwOnWrite is not null)
        {
            throw _throwOnWrite;
        }
    }
}
