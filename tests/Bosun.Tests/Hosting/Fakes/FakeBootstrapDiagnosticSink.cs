using Bosun.Hosting;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// In-memory <see cref="IBootstrapDiagnosticSink"/> for tests that only need to assert something
/// was recorded, without touching a real Windows Event Log or a real fallback file path
/// (CLAUDE.md worktree-safety rules). Can also be configured to throw, to prove callers survive a
/// broken diagnostic channel.
/// </summary>
public sealed class FakeBootstrapDiagnosticSink : IBootstrapDiagnosticSink
{
    private readonly Exception? _throwOnRecord;

    public FakeBootstrapDiagnosticSink(Exception? throwOnRecord = null)
    {
        _throwOnRecord = throwOnRecord;
    }

    public int CallCount { get; private set; }
    public string? LastReason { get; private set; }
    public Exception? LastException { get; private set; }

    public void Record(string reason, Exception? exception)
    {
        CallCount++;
        LastReason = reason;
        LastException = exception;

        if (_throwOnRecord is not null)
        {
            throw _throwOnRecord;
        }
    }
}
