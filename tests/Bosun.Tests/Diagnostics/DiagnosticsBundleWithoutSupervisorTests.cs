using System.IO.Compression;
using System.Text;
using Bosun.Diagnostics;
using Bosun.Health;
using Bosun.Hosting;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Diagnostics.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.UI.Tray.Fakes;

namespace Bosun.Tests.Diagnostics;

/// <summary>
/// bs-1kt: the bundle is most needed when the mount supervisor (or the rc client, or the config store
/// under it) could not be built, so it must be collectable without them. Everything here uses fakes and
/// a temp directory: no real process table, Event Log, rclone, drive, Explorer or profile path.
/// </summary>
public sealed class DiagnosticsBundleWithoutSupervisorTests : IDisposable
{
    private const string PlantedPassword = "PlantedPw+/Zq9=Secret";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bosun-1kt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _logs;
    private readonly string _configPath;

    private readonly FakeAppHealth _health = new(AppHealth.Healthy);
    private readonly FakePortOwnerResolver _portOwner = new();
    private readonly FakeProcessInspector _inspector = new();
    private readonly FakeProcessLister _lister = new();
    private readonly FakeDriveLister _drives = new();
    private readonly FakeWindowsEventSource _events = new();
    private readonly FakeEnvironmentInfoSource _environment = new();
    private readonly FakeTimeProvider _time = new(Now);

    private Func<IMountSupervisor> _supervisor = () => new FakeMountSupervisor();
    private Func<IRcloneClient> _rclone = () => new FakeRcloneClient();
    private Func<int> _rcPort = () => 5572;
    private Func<StartupReadiness?>? _startup;

    public DiagnosticsBundleWithoutSupervisorTests()
    {
        _logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logs);
        _configPath = Path.Combine(_root, "hosts.toml");
        File.WriteAllText(_configPath, "[global]\nrclone_rc_port = 5572\n");
        var log = Path.Combine(_logs, "bosun-20261101.log");
        File.WriteAllText(log, "the log tail that must survive a dead supervisor\n");
        File.SetLastWriteTimeUtc(log, Now.UtcDateTime.AddMinutes(-5));
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
            OutputDirectory = Path.Combine(_root, "out"),
            LogDirectory = _logs,
            ConfigPath = _configPath,
            RcPort = () => _rcPort(),
        },
        _health,
        () => _supervisor(),
        () => _rclone(),
        _portOwner,
        _inspector,
        _lister,
        _drives,
        _events,
        _environment,
        new RcloneRcCredential("planted-user-7f3a", PlantedPassword),
        _time,
        _startup);

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

    private static StartupReadiness Readiness(string? supervisorFault = null, string[]? configErrors = null) => new()
    {
        ConfigState = configErrors is null ? ConfigReadinessState.Loaded : ConfigReadinessState.Invalid,
        ConfigErrors = configErrors ?? [],
        TerminalFragmentWritten = true,
        WinFspInstalled = true,
        WinFspMessage = "WinFsp 2.0 is installed",
        RcloneHealthy = true,
        SupervisorRunning = supervisorFault is null,
        SupervisorFaultMessage = supervisorFault,
    };

    [Fact]
    public async Task A_supervisor_that_cannot_be_built_leaves_the_hosts_section_unavailable_with_the_reason_and_every_other_section_intact()
    {
        _supervisor = () => throw new InvalidOperationException("Unable to resolve service for type 'System.TimeProvider'");
        _lister.Set("rclone", 7);

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Contains("unavailable: InvalidOperationException: Unable to resolve service for type 'System.TimeProvider'", files["hosts.json"]);
        Assert.Contains("HOSTS: unavailable: InvalidOperationException: Unable to resolve service for type 'System.TimeProvider'", files["summary.txt"]);
        // Absence of a supervisor is the known fault, explained by health and startup.json, not a
        // section that failed to collect.
        Assert.Empty(result.Problems);
        foreach (var other in new[]
        {
            "health.json", "startup.json", "processes.json", "drives.json", "events.json", "version.txt",
            "config.redacted.toml", "logs/bosun-20261101.log",
        })
        {
            Assert.True(files.ContainsKey(other), $"{other} is missing");
        }

        Assert.Contains("the log tail that must survive a dead supervisor", files["logs/bosun-20261101.log"]);
        Assert.Contains("\"pid\": 7", files["processes.json"]);
    }

    [Fact]
    public async Task With_no_supervisor_the_hosts_file_does_not_pretend_there_are_no_hosts()
    {
        _supervisor = () => throw new InvalidOperationException("no supervisor");

        var hosts = ReadZip((await CreateBuilder().BuildAsync()).Path)["hosts.json"];

        Assert.Contains("\"hosts\": null", hosts);
        Assert.Contains("\"recentTransitions\": null", hosts);
        Assert.Contains("\"supervisorUnavailable\": \"unavailable: InvalidOperationException: no supervisor\"", hosts);
    }

    [Fact]
    public async Task An_rc_client_that_cannot_be_built_reads_as_unavailable_in_the_version_and_is_not_a_collection_problem()
    {
        _rclone = () => throw new InvalidOperationException("hosts.toml is invalid");

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Contains("rclone: unavailable (InvalidOperationException: hosts.toml is invalid)", files["version.txt"]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task An_rc_port_that_cannot_be_read_costs_the_owner_lookup_and_not_the_process_list()
    {
        _rcPort = () => throw new InvalidOperationException("hosts.toml is invalid");
        _lister.Set("rclone", 7);
        _inspector.Set(new Bosun.Rclone.Process.ProcessDescription { ProcessId = 7, Name = "rclone.exe" }, 7);

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Contains("\"pid\": 7", files["processes.json"]);
        Assert.Contains("\"rcPortUnavailable\": \"unavailable: InvalidOperationException: hosts.toml is invalid\"", files["processes.json"]);
        Assert.Contains("RC PORT: unavailable: InvalidOperationException: hosts.toml is invalid", files["summary.txt"]);
        Assert.Equal(0, _portOwner.CallCount);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task The_supervisors_own_start_failure_is_in_startup_json_and_the_summary()
    {
        _supervisor = () => throw new InvalidOperationException("cannot build");
        _startup = () => Readiness(supervisorFault: "Unable to resolve service for type 'System.TimeProvider'");

        var files = ReadZip((await CreateBuilder().BuildAsync()).Path);

        Assert.Contains("\"supervisorFaultMessage\": \"Unable to resolve service for type 'System.TimeProvider'\"", files["startup.json"]);
        Assert.Contains("\"supervisorStartFailed\": true", files["startup.json"]);
        Assert.Contains("supervisor: FAILED TO START", files["summary.txt"]);
        Assert.Contains("Unable to resolve service for type 'System.TimeProvider'", files["summary.txt"]);
    }

    [Fact]
    public async Task Config_errors_and_the_other_startup_state_are_reported_without_the_config_store()
    {
        _supervisor = () => throw new InvalidOperationException("cannot build");
        _rcPort = () => throw new InvalidOperationException("cannot read the port");
        _startup = () => Readiness(configErrors: ["line 3, column 1: unexpected token"]);

        var files = ReadZip((await CreateBuilder().BuildAsync()).Path);

        Assert.Contains("config: Invalid", files["summary.txt"]);
        Assert.Contains("line 3, column 1: unexpected token", files["summary.txt"]);
        Assert.Contains("WinFsp: installed -- WinFsp 2.0 is installed", files["summary.txt"]);
        Assert.Contains("\"configState\": \"Invalid\"", files["startup.json"]);
    }

    [Fact]
    public async Task With_no_startup_readiness_the_section_says_so_rather_than_being_absent()
    {
        _startup = null;

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Contains("unavailable: no startup readiness has been published", files["startup.json"]);
        Assert.Contains("STARTUP: unavailable: no startup readiness has been published", files["summary.txt"]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task A_startup_source_that_throws_is_a_collection_problem_and_the_bundle_still_completes()
    {
        _startup = () => throw new InvalidOperationException("orchestrator exploded");

        var result = await CreateBuilder().BuildAsync();
        var files = ReadZip(result.Path);

        Assert.Single(result.Problems);
        Assert.Contains("startup.json could not be collected: InvalidOperationException: orchestrator exploded", files["summary.txt"]);
        Assert.Contains("collectionFailed", files["startup.json"]);
        Assert.True(files.ContainsKey("hosts.json"));
    }

    [Fact]
    public async Task Redaction_still_applies_to_the_new_startup_section_and_to_the_config_when_there_is_no_supervisor()
    {
        _supervisor = () => throw new InvalidOperationException("cannot build");
        _startup = () => Readiness(supervisorFault: $"rclone rcd failed: --rc-pass={PlantedPassword} was rejected");
        File.WriteAllText(_configPath, "[hosts.example]\nhostname = \"files.example.net\"\npassword = \"hunter2\"\n");

        var files = ReadZip((await CreateBuilder().BuildAsync()).Path);

        foreach (var (_, text) in files)
        {
            Assert.DoesNotContain(PlantedPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        }

        Assert.Contains("files.example.net", files["config.redacted.toml"]);
        Assert.Contains("password = \"***\"", files["config.redacted.toml"]);
    }
}
