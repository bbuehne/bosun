using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bosun.Diagnostics;
using Bosun.Health;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Diagnostics.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.UI.Tray.Fakes;

namespace Bosun.Tests.Diagnostics;

/// <summary>
/// The bundle's contents and its redaction guarantee, against fakes and a temp directory only: no
/// real process table, Event Log, rclone, drive, Explorer or profile path is read or written.
/// </summary>
public sealed class DiagnosticsBundleBuilderTests : IDisposable
{
    // Chosen to be unmistakable, and to contain '+' and '/' so JSON escaping and URL escaping both
    // have something to get wrong.
    private const string PlantedUser = "planted-user-7f3a";
    private const string PlantedPassword = "PlantedPw+/Zq9=Secret";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bosun-ds3-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _logs;
    private readonly string _output;
    private readonly string _configPath;

    private readonly FakeAppHealth _health = new(AppHealth.Healthy);
    private readonly FakeMountSupervisor _supervisor = new();
    private readonly FakeRcloneClient _rclone = new();
    private readonly FakePortOwnerResolver _portOwner = new();
    private readonly FakeProcessInspector _inspector = new();
    private readonly FakeProcessLister _lister = new();
    private readonly FakeDriveLister _drives = new();
    private readonly FakeWindowsEventSource _events = new();
    private readonly FakeEnvironmentInfoSource _environment = new();
    private readonly FakeTimeProvider _time = new(Now);
    private RcloneRcCredential? _credential = new(PlantedUser, PlantedPassword);

    public DiagnosticsBundleBuilderTests()
    {
        _logs = Path.Combine(_root, "logs");
        _output = Path.Combine(_root, "out", "diagnostics");
        _configPath = Path.Combine(_root, "hosts.toml");
        Directory.CreateDirectory(_logs);
        File.WriteAllText(_configPath, "[global]\nrclone_rc_port = 5572\n");
        WriteLog("bosun-20261001.log", "ordinary line\n", Now.UtcDateTime.AddHours(-1));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DiagnosticsBundleBuilder CreateBuilder() => new(
        new DiagnosticsBundleOptions
        {
            OutputDirectory = _output,
            LogDirectory = _logs,
            ConfigPath = _configPath,
            RcPort = () => 5572,
        },
        _health,
        _supervisor,
        _rclone,
        _portOwner,
        _inspector,
        _lister,
        _drives,
        _events,
        _environment,
        _credential,
        _time);

    private void WriteLog(string name, string content, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_logs, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var reader = new StreamReader(e.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            });
    }

    private async Task<Dictionary<string, string>> BuildAndRead()
    {
        var result = await CreateBuilder().BuildAsync();
        return ReadZip(result.Path);
    }

    private static HealthIssue Issue(string code, HealthLevel level, string title, string detail) =>
        new(code, level, title, detail, Now.AddMinutes(-5));

    // ---- structure --------------------------------------------------------------------------

    [Fact]
    public async Task Writes_a_timestamped_zip_into_the_output_directory_containing_every_expected_entry()
    {
        var result = await CreateBuilder().BuildAsync();

        Assert.Equal(_output, Path.GetDirectoryName(result.Path));
        Assert.Matches(@"^bosun-diagnostics-\d{8}-\d{6}\.zip$", Path.GetFileName(result.Path));

        var entries = ReadZip(result.Path).Keys.ToHashSet();
        foreach (var expected in new[]
        {
            "summary.txt", "health.json", "hosts.json", "processes.json", "drives.json", "events.json",
            "version.txt", "config.redacted.toml", "logs/bosun-20261001.log",
        })
        {
            Assert.Contains(expected, entries);
        }

        Assert.Empty(result.Problems);
        Assert.Empty(Directory.GetFiles(_output, "*.tmp"));
    }

    [Fact]
    public async Task Two_bundles_in_the_same_second_get_different_files()
    {
        var first = await CreateBuilder().BuildAsync();
        var second = await CreateBuilder().BuildAsync();

        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task Summary_lists_the_health_issues_first_with_their_state()
    {
        _health.Value = new AppHealth
        {
            Level = HealthLevel.Faulted,
            Issues =
            [
                Issue(HealthIssueCodes.RclonePortHeld, HealthLevel.Faulted, "rclone port is held", "PID 4321 holds 127.0.0.1:5572"),
                Issue(HealthIssueCodes.StartupWinFspMissing, HealthLevel.Degraded, "WinFsp is missing", "install WinFsp"),
            ],
        };
        _supervisor.Snapshot = [new HostMountSnapshot { HostKey = "example", State = MountState.Unreachable, AdministrativelyEnabled = true }];

        var summary = (await BuildAndRead())["summary.txt"];

        Assert.Contains("HEALTH: Faulted -- 2 issue(s)", summary);
        Assert.Contains("[Faulted] rclone.port-held: rclone port is held", summary);
        Assert.Contains("PID 4321 holds 127.0.0.1:5572", summary);
        Assert.Contains("[Degraded] startup.winfsp-missing: WinFsp is missing", summary);
        Assert.True(summary.IndexOf("HEALTH:", StringComparison.Ordinal) < summary.IndexOf("HOSTS (", StringComparison.Ordinal));
        Assert.True(summary.IndexOf("HEALTH:", StringComparison.Ordinal) < summary.IndexOf("PROCESSES", StringComparison.Ordinal));
        Assert.Contains("COLLECTION PROBLEMS: none", summary);
    }

    [Fact]
    public async Task Health_json_is_the_current_health_model()
    {
        _health.Value = new AppHealth
        {
            Level = HealthLevel.Degraded,
            Issues = [Issue(HealthIssueCodes.StartupConfigInvalid, HealthLevel.Degraded, "config is invalid", "line 3")],
        };

        using var doc = JsonDocument.Parse((await BuildAndRead())["health.json"]);

        Assert.Equal("Degraded", doc.RootElement.GetProperty("level").GetString());
        var issue = doc.RootElement.GetProperty("issues")[0];
        Assert.Equal("startup.config-invalid", issue.GetProperty("code").GetString());
        Assert.Equal("line 3", issue.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Hosts_json_has_state_drive_last_transition_failure_counters_and_last_mount_failure()
    {
        var at = new DateTimeOffset(2026, 10, 1, 14, 30, 0, TimeSpan.Zero);
        _supervisor.Snapshot =
        [
            new HostMountSnapshot
            {
                HostKey = "example",
                State = MountState.Unreachable,
                Drive = "P:",
                AdministrativelyEnabled = true,
                ConsecutiveMountedFailures = 1,
                ConsecutiveDeepProbeFailures = 2,
                ConsecutiveIdleFailures = 3,
                ConsecutiveMountFailures = 4,
                LastTransitionUtc = at,
                LastTransitionTrigger = "deep-probe-failed",
                LastMountFailureReason = "mount/mount timed out after 60s",
            },
        ];
        _supervisor.TransitionHistory =
        [
            new MountTransitionEntry { TimestampUtc = at, HostKey = "example", From = MountState.Mounted, To = MountState.Unreachable, Trigger = "deep-probe-failed" },
        ];

        var files = await BuildAndRead();
        using var doc = JsonDocument.Parse(files["hosts.json"]);
        var host = doc.RootElement.GetProperty("hosts")[0];

        Assert.Equal("example", host.GetProperty("hostKey").GetString());
        Assert.Equal("Unreachable", host.GetProperty("state").GetString());
        Assert.Equal("P:", host.GetProperty("drive").GetString());
        Assert.Equal("deep-probe-failed", host.GetProperty("lastTransitionTrigger").GetString());
        Assert.Equal(at, host.GetProperty("lastTransitionUtc").GetDateTimeOffset());
        Assert.Equal(2, host.GetProperty("consecutiveDeepProbeFailures").GetInt32());
        Assert.Equal(4, host.GetProperty("consecutiveMountFailures").GetInt32());
        Assert.Equal("mount/mount timed out after 60s", host.GetProperty("lastMountFailureReason").GetString());
        Assert.Equal("Mounted", doc.RootElement.GetProperty("recentTransitions")[0].GetProperty("from").GetString());

        Assert.Contains("example: Unreachable, drive P:", files["summary.txt"]);
        Assert.Contains("last transition 2026-10-01T14:30:00Z (deep-probe-failed)", files["summary.txt"]);
        Assert.Contains("last mount failure: mount/mount timed out after 60s", files["summary.txt"]);
    }

    [Fact]
    public async Task Processes_json_has_every_rclone_and_bosun_process_and_the_rc_port_owner()
    {
        var started = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        _lister.Set("rclone", 1111);
        _lister.Set("Bosun", 4242);
        _inspector.Set(new ProcessDescription
        {
            ProcessId = 1111,
            Name = "rclone.exe",
            ImagePath = @"C:\tools\rclone.exe",
            CommandLine = @"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:\x\rclone.conf",
            StartTime = started,
            Owner = @"HOST\someone",
        }, 1111);
        _inspector.Set(new ProcessDescription { ProcessId = 4242, Name = "Bosun.exe", ImagePath = @"C:\apps\Bosun.exe", CommandLine = "Bosun.exe", StartTime = started.AddDays(3) }, 4242);
        _portOwner.Default = 1111;

        var files = await BuildAndRead();
        using var doc = JsonDocument.Parse(files["processes.json"]);
        var root = doc.RootElement;

        Assert.Equal(5572, root.GetProperty("rcPort").GetInt32());
        Assert.Equal(1111, root.GetProperty("rcPortOwnerPid").GetInt32());
        Assert.Equal(started, root.GetProperty("rcPortOwner").GetProperty("startTime").GetDateTimeOffset());
        Assert.Equal(2, root.GetProperty("processes").GetArrayLength());

        var rclone = root.GetProperty("processes").EnumerateArray().Single(p => p.GetProperty("pid").GetInt32() == 1111);
        Assert.Equal(@"C:\tools\rclone.exe", rclone.GetProperty("imagePath").GetString());
        Assert.Contains("--rc-addr 127.0.0.1:5572", rclone.GetProperty("commandLine").GetString());
        Assert.False(rclone.GetProperty("isCurrentProcess").GetBoolean());
        Assert.True(root.GetProperty("processes").EnumerateArray().Single(p => p.GetProperty("pid").GetInt32() == 4242).GetProperty("isCurrentProcess").GetBoolean());

        Assert.Contains("RC PORT 5572: held by PID 1111 (rclone.exe, started 2026-09-28T08:00:00", files["summary.txt"]);
    }

    [Fact]
    public async Task Processes_json_says_so_when_nothing_holds_the_rc_port()
    {
        var files = await BuildAndRead();
        using var doc = JsonDocument.Parse(files["processes.json"]);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("rcPortOwnerPid").ValueKind);
        Assert.Contains("RC PORT 5572: nothing is listening", files["summary.txt"]);
    }

    [Fact]
    public async Task Drives_json_lists_the_logical_drives_and_their_types()
    {
        _drives.Drives.Add(new LogicalDriveEntry(@"C:\", "Fixed"));
        _drives.Drives.Add(new LogicalDriveEntry(@"P:\", "Network"));

        var files = await BuildAndRead();
        using var doc = JsonDocument.Parse(files["drives.json"]);

        Assert.Equal("Network", doc.RootElement[1].GetProperty("driveType").GetString());
        Assert.Contains("P: Network", files["summary.txt"]);
    }

    [Fact]
    public async Task Events_json_keeps_only_recent_events_that_mention_bosun_or_rclone()
    {
        _events.Events.Add(new WindowsEventEntry(Now.AddDays(-1), 1002, "Application Hang", "Error", "The program Bosun.exe version 1.0 stopped interacting with Windows"));
        _events.Events.Add(new WindowsEventEntry(Now.AddDays(-2), 1000, "Application Error", "Error", "Faulting application name: RCLONE.EXE"));
        _events.Events.Add(new WindowsEventEntry(Now.AddDays(-1), 1000, "Application Error", "Error", "Faulting application name: notepad.exe"));
        _events.Events.Add(new WindowsEventEntry(Now.AddDays(-8), 1001, "Windows Error Reporting", "Information", "Fault bucket for Bosun.exe, long ago"));

        var files = await BuildAndRead();
        using var doc = JsonDocument.Parse(files["events.json"]);

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1002, doc.RootElement.GetProperty("events")[0].GetProperty("eventId").GetInt32());
        Assert.DoesNotContain("notepad", files["events.json"]);
        Assert.DoesNotContain("long ago", files["events.json"]);
        Assert.Equal(Now.AddDays(-7), _events.LastSince);
    }

    [Fact]
    public async Task Version_txt_has_bosun_os_runtime_and_rclone_versions()
    {
        _rclone.EnqueueVersion(new RcloneVersionInfo { Version = "v1.75.0", Os = "windows", Arch = "amd64" });

        var version = (await BuildAndRead())["version.txt"];

        Assert.Contains("Bosun assembly version: 1.2.3.4", version);
        Assert.Contains("Bosun informational version: 1.2.3+abc123", version);
        Assert.Contains("OS: Windows 11 test", version);
        Assert.Contains(".NET runtime: .NET 10 test", version);
        Assert.Contains("rclone: v1.75.0 (windows/amd64)", version);
    }

    [Fact]
    public async Task Version_txt_says_unavailable_when_rclone_does_not_answer_and_that_is_not_a_problem()
    {
        _rclone.EnqueueVersionFailure(new HttpRequestException("401 Unauthorized from another process"));

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Contains("rclone: unavailable (HttpRequestException: 401 Unauthorized from another process)", files["version.txt"]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task Config_keeps_hostnames_users_and_identity_files_but_masks_secret_looking_values()
    {
        File.WriteAllText(_configPath, """
            [hosts.example]
            hostname = "files.example.net"
            user = "someuser"
            identity_file = "~/.ssh/id_example"
            password = "hunter2"
            """);

        var config = (await BuildAndRead())["config.redacted.toml"];

        Assert.Contains("files.example.net", config);
        Assert.Contains("someuser", config);
        Assert.Contains("~/.ssh/id_example", config);
        Assert.DoesNotContain("hunter2", config);
        Assert.Contains("password = \"***\"", config);
    }

    // ---- redaction --------------------------------------------------------------------------

    [Fact]
    public async Task The_planted_credential_is_absent_from_every_entry_wherever_it_was_planted()
    {
        // In a log line, in a command line, in the config, and in health / failure text.
        WriteLog("bosun-20260930.log", $"[ERR] call failed with {PlantedPassword} as the password{Environment.NewLine}auth {PlantedUser}:{PlantedPassword}{Environment.NewLine}", Now.UtcDateTime.AddDays(-1));
        _lister.Set("rclone", 1111);
        _inspector.Set(new ProcessDescription
        {
            ProcessId = 1111,
            Name = "rclone.exe",
            // The live credential in the rc flags, plus a stale instance's credential that no
            // literal match could know about (the orphaned rcd of 2026-10-01).
            CommandLine = $"rclone.exe rcd --rc-user={PlantedUser} --rc-pass={PlantedPassword} --rc-addr 127.0.0.1:5572 --rc-pass stale-secret-1 --rc-user stale-user-1",
        }, 1111);
        File.WriteAllText(_configPath, $"""
            [hosts.example]
            hostname = "files.example.net"
            password = "{PlantedPassword}"
            note = "copied from a log: {PlantedPassword}"
            RCLONE_RC_PASS = "stale-secret-2"
            """);
        _health.Value = new AppHealth
        {
            Level = HealthLevel.Faulted,
            Issues = [Issue(HealthIssueCodes.RcloneUnauthorized, HealthLevel.Faulted, "rclone refused us", $"rcd said 401 to {PlantedUser}:{PlantedPassword}")],
        };
        _supervisor.Snapshot =
        [
            new HostMountSnapshot { HostKey = "example", State = MountState.Unreachable, AdministrativelyEnabled = true, LastMountFailureReason = $"mount failed: --rc-pass {PlantedPassword}" },
        ];
        _rclone.EnqueueVersionFailure(new InvalidOperationException($"bad credential {PlantedUser}:{PlantedPassword}"));
        _events.Events.Add(new WindowsEventEntry(Now.AddHours(-1), 1002, "Application Hang", "Error", $"Bosun.exe hung; env had RCLONE_RC_PASS={PlantedPassword}"));

        var files = await BuildAndRead();

        var jsonForm = JsonEncodedText.Encode(PlantedPassword).ToString();
        foreach (var (name, content) in files)
        {
            Assert.False(content.Contains(PlantedPassword, StringComparison.Ordinal), $"{name} contains the planted password");
            Assert.False(content.Contains(jsonForm, StringComparison.Ordinal), $"{name} contains the JSON-escaped planted password");
            Assert.False(content.Contains(PlantedUser, StringComparison.Ordinal), $"{name} contains the planted user name");
            Assert.False(content.Contains("stale-secret", StringComparison.Ordinal), $"{name} contains a stale instance's secret");
            Assert.False(content.Contains("stale-user", StringComparison.Ordinal), $"{name} contains a stale instance's user");
        }

        // The log copy and the command line are still useful: only the secret went.
        Assert.Contains("[ERR] call failed with *** as the password", files["logs/bosun-20260930.log"]);
        Assert.Contains("--rc-addr 127.0.0.1:5572", files["processes.json"]);
        Assert.Contains("--rc-pass=***", files["processes.json"]);
        Assert.Contains("--rc-pass ***", files["processes.json"]);
        Assert.Contains("files.example.net", files["config.redacted.toml"]);
    }

    [Theory]
    [InlineData("--rc-pass=x", "--rc-pass=***")]
    [InlineData("--rc-pass x", "--rc-pass ***")]
    public async Task Rc_pass_flag_values_are_redacted_in_command_lines_in_both_spellings(string flag, string redacted)
    {
        _credential = null;
        _lister.Set("rclone", 7);
        _inspector.Set(new ProcessDescription { ProcessId = 7, Name = "rclone.exe", CommandLine = $"rclone.exe rcd {flag} --rc-addr 127.0.0.1:5572" }, 7);

        var files = await BuildAndRead();

        Assert.DoesNotContain(flag, files["processes.json"]);
        Assert.Contains($"rclone.exe rcd {redacted} --rc-addr 127.0.0.1:5572", files["processes.json"]);
    }

    [Fact]
    public async Task Environment_variables_are_never_collected()
    {
        // Set a variable that would be an obvious leak if anything enumerated the environment.
        const string name = "BOSUN_DS3_TEST_ENV_CANARY";
        Environment.SetEnvironmentVariable(name, "canary-value-should-never-appear");
        try
        {
            var files = await BuildAndRead();

            Assert.DoesNotContain(files.Values, content => content.Contains("canary-value-should-never-appear", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ---- independence -----------------------------------------------------------------------

    [Fact]
    public async Task One_section_failing_is_noted_in_the_summary_and_the_bundle_still_completes()
    {
        _events.Throws = new InvalidOperationException("The Event Log service is not running");

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Single(result.Problems);
        Assert.Contains("events.json could not be collected: InvalidOperationException: The Event Log service is not running", files["summary.txt"]);
        Assert.Contains("COLLECTION PROBLEMS (1)", files["summary.txt"]);
        Assert.Contains("collectionFailed", files["events.json"]);
        foreach (var other in new[] { "health.json", "hosts.json", "processes.json", "drives.json", "version.txt", "config.redacted.toml", "logs/bosun-20261001.log" })
        {
            Assert.True(files.ContainsKey(other), $"{other} is missing");
        }
    }

    [Fact]
    public async Task Every_collector_can_fail_at_once_and_a_bundle_with_a_summary_is_still_written()
    {
        _health.Throws = new InvalidOperationException("health boom");
        _lister.Throws = new InvalidOperationException("lister boom");
        _drives.Throws = new InvalidOperationException("drives boom");
        _events.Throws = new InvalidOperationException("events boom");
        _portOwner.EnqueueThrow(new InvalidOperationException("port boom"));
        File.Delete(_configPath);
        Directory.Delete(_logs, recursive: true);

        var result = await CreateBuilder().BuildAsync();
        var summary = ReadZip(result.Path)["summary.txt"];

        Assert.Contains("health.json could not be collected", summary);
        Assert.Contains("drives.json could not be collected", summary);
        Assert.Contains("events.json could not be collected", summary);
        Assert.Contains("config.redacted.toml could not be collected", summary);
        Assert.Contains("logs/ could not be collected", summary);
        Assert.Contains("listing rclone.exe failed", summary);
        Assert.Contains("rc port 5572 owner lookup failed: InvalidOperationException: port boom", summary);
    }

    [Fact]
    public async Task A_process_the_inspector_cannot_describe_is_recorded_without_losing_the_others()
    {
        _lister.Set("rclone", 1, 2);
        _inspector.SetThrows(1, new InvalidOperationException("access denied"));
        _inspector.Set(new ProcessDescription { ProcessId = 2, Name = "rclone.exe" }, 2);

        var processes = (await BuildAndRead())["processes.json"];

        Assert.Contains("access denied", processes);
        Assert.Contains("\"pid\": 2", processes);
    }

    [Fact]
    public async Task A_failure_to_write_the_zip_at_all_is_thrown_and_leaves_no_partial_file()
    {
        // The output "directory" is a file, so nothing can be created there.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var builder = new DiagnosticsBundleBuilder(
            new DiagnosticsBundleOptions { OutputDirectory = blocker, LogDirectory = _logs, ConfigPath = _configPath, RcPort = () => 5572 },
            _health, _supervisor, _rclone, _portOwner, _inspector, _lister, _drives, _events, _environment, _credential, _time);

        await Assert.ThrowsAnyAsync<IOException>(() => builder.BuildAsync());
    }

    // ---- logs -------------------------------------------------------------------------------

    [Fact]
    public async Task A_log_still_held_open_for_writing_by_another_stream_is_copied()
    {
        // Serilog's file sink holds the live file open for writing and shares it for reading only.
        var live = Path.Combine(_logs, "bosun-20261001.log");
        using var writer = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes("live line one\nlive line two\n");
        writer.Write(bytes);
        writer.Flush();
        File.SetLastWriteTimeUtc(live, Now.UtcDateTime.AddMinutes(-1));

        var files = await BuildAndRead();

        Assert.Contains("live line one", files["logs/bosun-20261001.log"]);
        Assert.Contains("live line two", files["logs/bosun-20261001.log"]);
    }

    [Fact]
    public async Task Logs_older_than_three_days_and_files_that_are_not_logs_are_excluded()
    {
        WriteLog("bosun-20260929.log", "two days old\n", Now.UtcDateTime.AddDays(-2));
        WriteLog("bosun-20260927.log", "four days old\n", Now.UtcDateTime.AddDays(-4));
        WriteLog("bosun-20260901.log", "a month old\n", Now.UtcDateTime.AddDays(-30));
        WriteLog("notes.txt", "not a log\n", Now.UtcDateTime.AddHours(-1));

        var entries = (await BuildAndRead()).Keys.Where(k => k.StartsWith("logs/", StringComparison.Ordinal)).Order().ToArray();

        Assert.Equal(["logs/bosun-20260929.log", "logs/bosun-20261001.log"], entries);
    }

    [Fact]
    public async Task Log_retention_follows_the_options()
    {
        WriteLog("bosun-20260929.log", "two days old\n", Now.UtcDateTime.AddDays(-2));
        var options = new DiagnosticsBundleOptions
        {
            OutputDirectory = _output,
            LogDirectory = _logs,
            ConfigPath = _configPath,
            RcPort = () => 5572,
            LogRetention = TimeSpan.FromDays(1),
        };
        var builder = new DiagnosticsBundleBuilder(
            options, _health, _supervisor, _rclone, _portOwner, _inspector, _lister, _drives, _events, _environment, _credential, _time);

        var entries = ReadZip((await builder.BuildAsync()).Path).Keys.Where(k => k.StartsWith("logs/", StringComparison.Ordinal)).ToArray();

        Assert.Equal(["logs/bosun-20261001.log"], entries);
    }
}
