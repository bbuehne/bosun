using System.Diagnostics;

namespace Bosun.Hosting;

/// <summary>
/// Production implementation of <see cref="IEventLogWriter"/>: writes to the Windows "Application"
/// event log under the "Bosun" source.
/// </summary>
/// <remarks>
/// <see cref="EventLog.WriteEntry(string,string,EventLogEntryType)"/> auto-creates the event
/// source the first time it is used, and creating a new source requires administrative rights on
/// modern Windows -- Bosun deliberately never requires elevation (ADR-010), so on most machines
/// this throws the first time it runs. That is expected and is exactly why
/// <see cref="BootstrapDiagnosticSink"/> treats this as best-effort and falls back to a file. If
/// the source is ever pre-registered (e.g. by a future installer), the same call starts
/// succeeding with no code change.
/// </remarks>
public sealed class Win32EventLogWriter : IEventLogWriter
{
    public void Write(string source, string message)
    {
        EventLog.WriteEntry(source, message, EventLogEntryType.Error);
    }
}
