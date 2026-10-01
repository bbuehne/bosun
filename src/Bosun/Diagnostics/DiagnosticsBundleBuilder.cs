using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bosun.Health;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Supervisor;
using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics;

/// <summary>Where and how far back the bundle looks (bs-ds3).</summary>
public sealed record DiagnosticsBundleOptions
{
    /// <summary>Where zips are written: <c>%LOCALAPPDATA%\Bosun\diagnostics</c> in production, a temp
    /// directory in tests.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>The Serilog directory, <c>%LOCALAPPDATA%\Bosun\logs</c>.</summary>
    public required string LogDirectory { get; init; }

    /// <summary>The <c>hosts.toml</c> path.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>The rc port, read at build time so a reloaded config is honoured.</summary>
    public required Func<int> RcPort { get; init; }

    public TimeSpan LogRetention { get; init; } = TimeSpan.FromDays(3);

    public TimeSpan EventLookback { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long to wait for <c>core/version</c>. A wedged or foreign rcd must not make a
    /// diagnostics click hang.</summary>
    public TimeSpan RcloneVersionTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The diagnostics directory that sits beside the log directory: with the production
    /// <c>%LOCALAPPDATA%\Bosun\logs</c> that is <c>%LOCALAPPDATA%\Bosun\diagnostics</c>, and with a
    /// test's temp log directory it stays inside the temp tree, so nothing a test builds lands in
    /// the real profile.
    /// </summary>
    public static DiagnosticsBundleOptions ForLogDirectory(string logDirectory, string configPath, Func<int> rcPort)
    {
        var trimmed = logDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new DiagnosticsBundleOptions
        {
            OutputDirectory = Path.Combine(Path.GetDirectoryName(trimmed) ?? trimmed, "diagnostics"),
            LogDirectory = logDirectory,
            ConfigPath = configPath,
            RcPort = rcPort,
        };
    }
}

/// <param name="Path">The zip that was written.</param>
/// <param name="Problems">One line per section that could not be collected. Empty when everything
/// was. These are also in the zip's <c>summary.txt</c>.</param>
public sealed record DiagnosticsBundleResult(string Path, IReadOnlyList<string> Problems);

public interface IDiagnosticsBundleBuilder
{
    /// <summary>Writes the bundle. A section that fails is recorded and skipped; this throws only
    /// when no zip can be written at all (output directory not writable, disk full).</summary>
    Task<DiagnosticsBundleResult> BuildAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes the diagnostics zip (bs-ds3, ADR-020 §6): the evidence the 2026-10-01 diagnosis had to
/// gather by hand, captured in one click while the failure is still happening.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> <c>summary.txt</c> (open this first: health issues, then everything else in
/// brief, then any collection problems), <c>health.json</c>, <c>hosts.json</c>,
/// <c>processes.json</c>, <c>drives.json</c>, <c>events.json</c>, <c>version.txt</c>,
/// <c>config.redacted.toml</c> and <c>logs/</c>.
/// </para>
/// <para>
/// <b>Independence.</b> Every section is collected in its own try block. A failure is written into
/// <c>summary.txt</c> (and, for a JSON file, into the file itself as <c>collectionFailed</c>) and
/// the bundle still completes: the bundle matters most when Bosun is already misbehaving, which is
/// when a collector is most likely to fail.
/// </para>
/// <para>
/// <b>Redaction.</b> Every text goes through <see cref="DiagnosticsRedactor"/> inside
/// <see cref="ZipSink"/>, the only thing that writes to the archive. The model strings that carry
/// command lines are also redacted before they are serialised, because a quoted value inside a
/// command line is JSON-escaped on the way out and a pattern run afterwards cannot see the quote it
/// ends at.
/// </para>
/// <para>
/// <b>Safety.</b> Read-only throughout: no mount, unmount or rc mutation, and no touching a drive
/// letter (see <see cref="IDriveLister"/>). The one rc call is <c>core/version</c>, bounded by
/// <see cref="DiagnosticsBundleOptions.RcloneVersionTimeout"/>.
/// </para>
/// </remarks>
public sealed class DiagnosticsBundleBuilder : IDiagnosticsBundleBuilder
{
    /// <summary>1000 Application Error, 1001 Windows Error Reporting, 1002 Application Hang.</summary>
    internal static readonly int[] EventIds = [1000, 1001, 1002];

    private static readonly string[] EventProgramMarkers = ["Bosun.exe", "rclone.exe"];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Relaxed so a '+' in a value is not written as + (which a literal match would miss).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DiagnosticsBundleOptions _options;
    private readonly IAppHealth _health;
    private readonly IMountSupervisor _supervisor;
    private readonly IRcloneClient _rclone;
    private readonly IPortOwnerResolver _portOwnerResolver;
    private readonly IProcessInspector _processInspector;
    private readonly IProcessLister _processLister;
    private readonly IDriveLister _driveLister;
    private readonly IWindowsEventSource _eventSource;
    private readonly IEnvironmentInfoSource _environment;
    private readonly DiagnosticsRedactor _redactor;
    private readonly TimeProvider _time;
    private readonly ILogger<DiagnosticsBundleBuilder>? _logger;

    public DiagnosticsBundleBuilder(
        DiagnosticsBundleOptions options,
        IAppHealth health,
        IMountSupervisor supervisor,
        IRcloneClient rclone,
        IPortOwnerResolver portOwnerResolver,
        IProcessInspector processInspector,
        IProcessLister processLister,
        IDriveLister driveLister,
        IWindowsEventSource eventSource,
        IEnvironmentInfoSource environment,
        RcloneRcCredential? credential,
        TimeProvider time,
        ILogger<DiagnosticsBundleBuilder>? logger = null)
    {
        _options = options;
        _health = health;
        _supervisor = supervisor;
        _rclone = rclone;
        _portOwnerResolver = portOwnerResolver;
        _processInspector = processInspector;
        _processLister = processLister;
        _driveLister = driveLister;
        _eventSource = eventSource;
        _environment = environment;
        _redactor = new DiagnosticsRedactor(credential);
        _time = time;
        _logger = logger;
    }

    public async Task<DiagnosticsBundleResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        // The only async collector, run first; everything else is synchronous I/O (CIM, Event Log,
        // files) and goes on the thread pool so the UI thread that clicked the button stays live.
        var rcloneVersion = await GetRcloneVersionAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => Write(rcloneVersion, cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    private async Task<string> GetRcloneVersionAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RcloneVersionTimeout);
        try
        {
            var version = await _rclone.GetVersionAsync(timeout.Token).ConfigureAwait(false);
            return string.Join(
                ' ',
                new[] { version.Version, version.Os is null ? null : $"({version.Os}/{version.Arch})" }
                    .Where(s => !string.IsNullOrEmpty(s)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Expected whenever rcd is down, wedged, or someone else's (the very failures this
            // bundle exists for), so it is information, not a collection problem.
            return $"unavailable ({ex.GetType().Name}: {FirstLine(ex.Message)})";
        }
    }

    private DiagnosticsBundleResult Write(string rcloneVersion, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.OutputDirectory);

        var now = _time.GetLocalNow();
        var finalPath = UniquePath(now);
        var tempPath = finalPath + ".tmp";
        var problems = new List<string>();

        try
        {
            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var sink = new ZipSink(zip, _redactor, now);
                Collect(sink, problems, now, rcloneVersion, cancellationToken);
            }

            File.Move(tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        _logger?.LogInformation("Wrote diagnostics bundle {Path} ({Problems} collection problem(s))", finalPath, problems.Count);
        return new DiagnosticsBundleResult(finalPath, problems);
    }

    private string UniquePath(DateTimeOffset local)
    {
        var stem = Path.Combine(
            _options.OutputDirectory,
            "bosun-diagnostics-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var path = stem + ".zip";
        for (var n = 2; File.Exists(path) || File.Exists(path + ".tmp"); n++)
        {
            path = $"{stem}-{n}.zip";
        }

        return path;
    }

    private void Collect(
        ZipSink sink, List<string> problems, DateTimeOffset now, string rcloneVersion, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();

        // --- Each block below is independent: a failure records a problem and the rest still run.
        var environment = Section("version.txt", problems, () => _environment.Get());
        var health = Section("health.json", problems, () => _health.Current);
        var hosts = Section("hosts.json", problems, () => CollectHosts(nowUtc, problems));
        ct.ThrowIfCancellationRequested();
        var processes = Section("processes.json", problems, () => CollectProcesses(problems));
        var drives = Section("drives.json", problems, () => _driveLister.GetDrives());
        ct.ThrowIfCancellationRequested();
        var events = Section("events.json", problems, () => CollectEvents(nowUtc));
        var logs = Section("logs/", problems, () => CopyLogs(sink, nowUtc, problems, ct));
        var config = Section("config.redacted.toml", problems, () => CollectConfig());

        sink.WriteJson("health.json", health.Value is { } h ? h : Failed(health.Error));
        sink.WriteJson("hosts.json", hosts.Value is { } hs ? hs : Failed(hosts.Error));
        sink.WriteJson("processes.json", processes.Value is { } p ? p : Failed(processes.Error));
        sink.WriteJson("drives.json", drives.Value is { } d ? d : Failed(drives.Error));
        sink.WriteJson("events.json", events.Value is { } e ? e : Failed(events.Error));
        sink.WriteText("version.txt", VersionText(environment.Value, rcloneVersion, nowUtc, environment.Error));
        sink.WriteText(
            "config.redacted.toml",
            config.Value is { } c ? c : $"# collection failed: {config.Error}{Environment.NewLine}");

        // Written last so it can list every problem above. Entry order in a zip means nothing to a
        // person opening it, and this is the file they open first.
        sink.WriteText(
            "summary.txt",
            Summary(now, environment.Value, health.Value, hosts.Value, processes.Value, drives.Value, events.Value,
                logs.Value, rcloneVersion, problems, sink.EntryNames));
    }

    private static object Failed(string? error) => new { collectionFailed = error ?? "unknown error" };

    private (T? Value, string? Error) Section<T>(string name, List<string> problems, Func<T> collect) where T : class
    {
        try
        {
            return (collect(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
            problems.Add($"{name} could not be collected: {message}");
            _logger?.LogWarning(ex, "Diagnostics section {Section} failed", name);
            return (null, message);
        }
    }

    // ---- hosts.json ------------------------------------------------------------------------

    private HostsReport CollectHosts(DateTimeOffset nowUtc, List<string> problems)
    {
        // The supervisor's snapshot is a lock-free read of its own fields, so it works even when
        // the loop is wedged, which is the situation that matters.
        var snapshot = _supervisor.GetSnapshot();

        IReadOnlyList<MountTransitionEntry>? transitions = null;
        string? transitionsError = null;
        try
        {
            transitions = _supervisor.GetTransitionHistory();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            transitionsError = $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
            problems.Add($"hosts.json transition history could not be collected: {transitionsError}");
        }

        return new HostsReport(nowUtc, snapshot.OrderBy(h => h.HostKey, StringComparer.OrdinalIgnoreCase).ToArray(), transitions ?? [], transitionsError);
    }

    private sealed record HostsReport(
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<HostMountSnapshot> Hosts,
        IReadOnlyList<MountTransitionEntry> RecentTransitions,
        string? TransitionsCollectionFailed);

    // ---- processes.json --------------------------------------------------------------------

    private ProcessesReport CollectProcesses(List<string> problems)
    {
        var currentPid = _environment.Get().CurrentProcessId;
        var records = new List<ProcessRecord>();
        string? listError = null;

        foreach (var image in new[] { "rclone", "Bosun" })
        {
            IReadOnlyList<int> pids;
            try
            {
                pids = _processLister.GetProcessIds(image);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                listError += $"{image}.exe: {ex.GetType().Name}: {FirstLine(ex.Message)}; ";
                problems.Add($"processes.json: listing {image}.exe failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
                continue;
            }

            foreach (var pid in pids.Distinct().Order())
            {
                records.Add(Describe(pid, currentPid));
            }
        }

        var port = _options.RcPort();
        ProcessRecord? owner = null;
        int? ownerPid = null;
        string? ownerError = null;
        try
        {
            ownerPid = _portOwnerResolver.GetListeningProcessId(port);
            if (ownerPid is { } pid)
            {
                owner = records.FirstOrDefault(r => r.Pid == pid) ?? Describe(pid, currentPid);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ownerError = $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
            problems.Add($"processes.json: rc port {port} owner lookup failed: {ownerError}");
        }

        return new ProcessesReport(port, ownerPid, owner, ownerError, listError, records);
    }

    private ProcessRecord Describe(int pid, int currentPid)
    {
        try
        {
            var description = _processInspector.Describe(pid);
            if (description is null)
            {
                return new ProcessRecord(pid, null, null, null, null, null, pid == currentPid, Exited: true, null);
            }

            return new ProcessRecord(
                pid,
                description.Name,
                description.StartTime,
                description.ImagePath,
                description.CommandLine is null ? null : _redactor.Redact(description.CommandLine),
                description.Owner,
                pid == currentPid,
                Exited: false,
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessRecord(
                pid, null, null, null, null, null, pid == currentPid, Exited: false, $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    private sealed record ProcessRecord(
        int Pid,
        string? Name,
        DateTimeOffset? StartTime,
        string? ImagePath,
        string? CommandLine,
        string? Owner,
        bool IsCurrentProcess,
        bool Exited,
        string? Error);

    private sealed record ProcessesReport(
        int RcPort,
        int? RcPortOwnerPid,
        ProcessRecord? RcPortOwner,
        string? RcPortOwnerError,
        string? ProcessListError,
        IReadOnlyList<ProcessRecord> Processes);

    // ---- events.json -----------------------------------------------------------------------

    private EventsReport CollectEvents(DateTimeOffset nowUtc)
    {
        var since = nowUtc - _options.EventLookback;
        var matching = _eventSource.ReadApplicationEvents(since, EventIds)
            .Where(e => e.TimeCreatedUtc >= since)
            .Where(e => EventProgramMarkers.Any(m => e.Message.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.TimeCreatedUtc)
            .ToArray();

        return new EventsReport(since, EventIds, matching.Length, matching);
    }

    private sealed record EventsReport(
        DateTimeOffset SinceUtc, IReadOnlyList<int> EventIds, int Count, IReadOnlyList<WindowsEventEntry> Events);

    // ---- logs/ -----------------------------------------------------------------------------

    private LogsReport CopyLogs(ZipSink sink, DateTimeOffset nowUtc, List<string> problems, CancellationToken ct)
    {
        var cutoff = nowUtc - _options.LogRetention;
        var copied = new List<string>();

        if (!Directory.Exists(_options.LogDirectory))
        {
            throw new DirectoryNotFoundException($"log directory {_options.LogDirectory} does not exist");
        }

        foreach (var path in Directory.EnumerateFiles(_options.LogDirectory, "*.log").Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified < cutoff.UtcDateTime)
            {
                continue;
            }

            var name = Path.GetFileName(path);
            try
            {
                // ReadWrite | Delete: Serilog holds the live file open for writing. Asking for
                // anything narrower fails with a sharing violation on exactly the file we most want.
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sink.WriteLog("logs/" + name, source, modified);
                copied.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"logs/{name} could not be copied: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }

        return new LogsReport(cutoff, copied);
    }

    private sealed record LogsReport(DateTimeOffset NewerThanUtc, IReadOnlyList<string> Files);

    // ---- config.redacted.toml --------------------------------------------------------------

    private string CollectConfig()
    {
        using var file = new FileStream(_options.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return _redactor.RedactToml(reader.ReadToEnd());
    }

    // ---- version.txt -----------------------------------------------------------------------

    private static string VersionText(EnvironmentInfo? info, string rcloneVersion, DateTimeOffset nowUtc, string? error)
    {
        var text = new StringBuilder();
        if (info is not null)
        {
            text.AppendLine($"Bosun assembly version: {info.AssemblyVersion}");
            text.AppendLine($"Bosun informational version: {info.InformationalVersion}");
            text.AppendLine($"OS: {info.OsDescription}");
            text.AppendLine($".NET runtime: {info.RuntimeDescription}");
        }
        else
        {
            text.AppendLine($"Bosun, OS and runtime versions: collection failed: {error}");
        }

        text.AppendLine($"rclone: {rcloneVersion}");
        text.AppendLine($"Bundle created (UTC): {nowUtc:O}");
        return text.ToString();
    }

    // ---- summary.txt -----------------------------------------------------------------------

    private static string Summary(
        DateTimeOffset now,
        EnvironmentInfo? info,
        AppHealth? health,
        HostsReport? hosts,
        ProcessesReport? processes,
        IReadOnlyList<LogicalDriveEntry>? drives,
        EventsReport? events,
        LogsReport? logs,
        string rcloneVersion,
        IReadOnlyList<string> problems,
        IReadOnlyList<string> entries)
    {
        var s = new StringBuilder();
        s.AppendLine("Bosun diagnostics bundle");
        s.AppendLine("========================");
        s.AppendLine($"Captured: {now:yyyy-MM-dd HH:mm:ss zzz} ({now.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ})");
        s.AppendLine(info is null
            ? "Bosun: version unavailable (see problems below)"
            : $"Bosun: {info.InformationalVersion} (assembly {info.AssemblyVersion}), pid {info.CurrentProcessId}");
        s.AppendLine($"rclone: {rcloneVersion}");
        s.AppendLine();

        // The issues come first: they are why the maintainer clicked the button.
        if (health is null)
        {
            s.AppendLine("HEALTH: unavailable (see problems below)");
        }
        else if (health.Issues.Count == 0)
        {
            s.AppendLine(health.IsStarting ? "HEALTH: OK (still starting)" : "HEALTH: OK -- no issues");
        }
        else
        {
            s.AppendLine($"HEALTH: {health.Level} -- {health.Issues.Count} issue(s){(health.IsStarting ? " (still starting)" : string.Empty)}");
            foreach (var issue in health.Issues)
            {
                s.AppendLine($"  [{issue.Severity}] {issue.Code}: {issue.Title}");
                s.AppendLine($"      {issue.Detail}");
                s.AppendLine($"      first seen {Utc(issue.FirstSeenUtc)}");
            }
        }

        s.AppendLine();
        if (hosts is null)
        {
            s.AppendLine("HOSTS: unavailable (see problems below)");
        }
        else
        {
            s.AppendLine($"HOSTS ({hosts.Hosts.Count})");
            foreach (var host in hosts.Hosts)
            {
                s.AppendLine(
                    $"  {host.HostKey}: {host.State}, drive {host.Drive ?? "-"}" +
                    (host.UserParked ? ", parked" : string.Empty) +
                    (host.AdministrativelyEnabled ? string.Empty : ", disabled"));
                s.AppendLine(
                    $"      last transition {(host.LastTransitionUtc is { } t ? Utc(t) : "never")}" +
                    $" ({host.LastTransitionTrigger ?? "no trigger recorded"})");
                s.AppendLine(
                    $"      failures: mounted {host.ConsecutiveMountedFailures}, deep probe {host.ConsecutiveDeepProbeFailures}, " +
                    $"idle {host.ConsecutiveIdleFailures}, mount {host.ConsecutiveMountFailures}");
                if (!string.IsNullOrEmpty(host.LastMountFailureReason))
                {
                    s.AppendLine($"      last mount failure: {host.LastMountFailureReason}");
                }

                if (!string.IsNullOrEmpty(host.MountUnavailableReason))
                {
                    s.AppendLine($"      mounting unavailable: {host.MountUnavailableReason}");
                }
            }
        }

        s.AppendLine();
        if (processes is null)
        {
            s.AppendLine("PROCESSES: unavailable (see problems below)");
        }
        else
        {
            s.AppendLine(processes.RcPortOwnerError is not null
                ? $"RC PORT {processes.RcPort}: owner lookup failed: {processes.RcPortOwnerError}"
                : processes.RcPortOwnerPid is { } ownerPid
                    ? $"RC PORT {processes.RcPort}: held by PID {ownerPid} ({processes.RcPortOwner?.Name ?? "unknown image"}, started {processes.RcPortOwner?.StartTime?.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture) ?? "unknown"})"
                    : $"RC PORT {processes.RcPort}: nothing is listening");
            if (processes.ProcessListError is not null)
            {
                s.AppendLine($"  process listing incomplete: {processes.ProcessListError}");
            }

            s.AppendLine($"PROCESSES ({processes.Processes.Count}); command lines are in processes.json");
            foreach (var process in processes.Processes)
            {
                s.AppendLine(
                    $"  PID {process.Pid} {process.Name ?? "?"}{(process.IsCurrentProcess ? " (this Bosun)" : string.Empty)}" +
                    (process.Exited ? " (exited)" : $", started {process.StartTime?.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture) ?? "unknown"}") +
                    (process.Error is null ? string.Empty : $", error: {process.Error}"));
            }
        }

        s.AppendLine();
        s.AppendLine(drives is null
            ? "DRIVES: unavailable (see problems below)"
            : $"DRIVES: {string.Join(", ", drives.Select(d => $"{d.Name.TrimEnd('\\')} {d.DriveType}"))}");

        s.AppendLine();
        if (events is null)
        {
            s.AppendLine("EVENTS: unavailable (see problems below)");
        }
        else
        {
            s.AppendLine($"EVENTS: {events.Count} Application hang/error event(s) mentioning Bosun.exe or rclone.exe since {Utc(events.SinceUtc)}");
            foreach (var evt in events.Events.Take(5))
            {
                s.AppendLine($"  {Utc(evt.TimeCreatedUtc)} event {evt.EventId} {evt.Provider}");
            }
        }

        s.AppendLine();
        s.AppendLine(logs is null
            ? "LOGS: unavailable (see problems below)"
            : $"LOGS: {logs.Files.Count} file(s) modified since {Utc(logs.NewerThanUtc)}: {string.Join(", ", logs.Files)}");

        s.AppendLine();
        if (problems.Count == 0)
        {
            s.AppendLine("COLLECTION PROBLEMS: none");
        }
        else
        {
            s.AppendLine($"COLLECTION PROBLEMS ({problems.Count})");
            foreach (var problem in problems)
            {
                s.AppendLine($"  - {problem}");
            }
        }

        s.AppendLine();
        s.AppendLine("Redaction: the rc credential, --rc-user/--rc-pass values, RCLONE_RC_* values, secret-looking config values and private key blocks are replaced with ***.");
        s.AppendLine("Environment variables are never collected.");
        s.AppendLine();
        s.AppendLine("FILES");
        foreach (var entry in entries.Order(StringComparer.Ordinal))
        {
            s.AppendLine($"  {entry}");
        }

        s.AppendLine("  summary.txt");
        return s.ToString();
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The only writer into the archive, and it redacts. Nothing else in this class holds the
    /// <see cref="ZipArchive"/>, so a text cannot reach the zip without passing
    /// <see cref="DiagnosticsRedactor"/>.
    /// </summary>
    private sealed class ZipSink(ZipArchive zip, DiagnosticsRedactor redactor, DateTimeOffset now)
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> EntryNames => _entries;

        public void WriteJson(string name, object value) =>
            WriteText(name, JsonSerializer.Serialize(value, Json));

        public void WriteText(string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = now;
            using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
            writer.Write(redactor.Redact(text));
            _entries.Add(name);
        }

        public void WriteLog(string name, Stream source, DateTime lastWriteUtc)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = lastWriteUtc < new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                ? now
                : new DateTimeOffset(lastWriteUtc);
            _entries.Add(name);

            using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
            using var reader = new StreamReader(source, Utf8NoBom, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            redactor.Redact(reader, writer);
        }
    }
}
