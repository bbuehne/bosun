namespace Bosun.Hosting;

/// <summary>
/// Thin seam over <see cref="System.Diagnostics.EventLog"/>, so <see cref="BootstrapDiagnosticSink"/>
/// can be unit tested without ever touching the real Windows Event Log (CLAUDE.md worktree-safety
/// rules: no default-suite test touches a real system). The production implementation is
/// <see cref="Win32EventLogWriter"/>.
/// </summary>
public interface IEventLogWriter
{
    /// <summary>Writes a single Error-level entry under <paramref name="source"/>. May throw.</summary>
    void Write(string source, string message);
}
