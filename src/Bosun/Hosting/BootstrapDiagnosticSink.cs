using System.IO;

namespace Bosun.Hosting;

/// <summary>
/// Production <see cref="IBootstrapDiagnosticSink"/>: attempts the Windows Event Log first, and
/// -- because that attempt can genuinely fail -- always also attempts a fixed fallback file,
/// regardless of whether the Event Log write succeeded. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the Event Log is the primary choice.</b> The failure this exists to catch is
/// specifically that <c>%LOCALAPPDATA%\Bosun\logs</c> (or whatever <c>LogDirectory</c> resolves
/// to) could not be created -- a permissions problem, a redirected profile, or a full volume
/// (bs-ipq). The Event Log is not rooted in that directory tree at all; it is a separate OS
/// subsystem, so it survives exactly the failure class this ADR worries about.
/// </para>
/// <para>
/// <b>Why there is a fallback anyway.</b> The Event Log write is not guaranteed to succeed
/// either: <see cref="Win32EventLogWriter"/>'s doc comment explains that registering a new event
/// source requires administrative rights, which Bosun never requires (ADR-010), so on a typical
/// machine the very first write throws. A single-sink design would then have zero durable record
/// of the failure it exists to catch -- the exact "no file, no debug sink, no dialog, nothing"
/// failure mode bs-ipq is about, just moved one level down. So this sink always also writes a
/// fixed fallback file, deliberately NOT under the caller-supplied <c>LogDirectory</c> (that path
/// is the very thing that may be broken) and not under <c>%LOCALAPPDATA%</c> at all, to avoid
/// depending on the same profile tree that produced the original failure. <see cref="Path.GetTempPath"/>
/// is used by default: it requires no directory creation beyond one child folder, needs no admin
/// rights, and is independent of whatever config value the caller was trying to build.
/// </para>
/// <para>
/// Both attempts are individually wrapped in <c>try/catch</c> so that a failure in one never
/// prevents the other, and neither can escape <see cref="Record"/> itself.
/// </para>
/// </remarks>
public sealed class BootstrapDiagnosticSink : IBootstrapDiagnosticSink
{
    private const string EventSource = "Bosun";

    private readonly IEventLogWriter _eventLogWriter;
    private readonly string _fallbackFilePath;

    /// <summary>
    /// Production instance: real Event Log, fallback file at
    /// <c>%TEMP%\Bosun\bootstrap-failures.log</c>.
    /// </summary>
    public BootstrapDiagnosticSink()
        : this(new Win32EventLogWriter(), Path.Combine(Path.GetTempPath(), "Bosun", "bootstrap-failures.log"))
    {
    }

    /// <summary>
    /// Test seam: both the Event Log writer and the fallback file path are injectable, so a test
    /// can force the Event Log path to fail and point the file path at a temp directory it owns --
    /// never the real Event Log or the real <c>%TEMP%</c>/<c>%LOCALAPPDATA%</c> (CLAUDE.md
    /// worktree-safety rules).
    /// </summary>
    public BootstrapDiagnosticSink(IEventLogWriter eventLogWriter, string fallbackFilePath)
    {
        _eventLogWriter = eventLogWriter;
        _fallbackFilePath = fallbackFilePath;
    }

    public void Record(string reason, Exception? exception)
    {
        var message = FormatMessage(reason, exception);

        TryWriteEventLog(message);
        TryWriteFallbackFile(message);
    }

    private void TryWriteEventLog(string message)
    {
        try
        {
            _eventLogWriter.Write(EventSource, message);
        }
        catch
        {
            // Best-effort: see the class doc comment. A missing/unregistered event source is the
            // expected case on an unelevated install; the fallback file below is what actually
            // carries the record in that case.
        }
    }

    private void TryWriteFallbackFile(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(_fallbackFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                _fallbackFilePath,
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort: see the class doc comment. If even this fails, Record() still must
            // not throw -- there is nothing left downstream to catch it.
        }
    }

    private static string FormatMessage(string reason, Exception? exception) =>
        exception is null ? reason : $"{reason}{Environment.NewLine}{exception}";
}
