using System.IO.Compression;
using System.Text;
using Bosun.Diagnostics;
using Bosun.Health;
using Bosun.Rclone;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Diagnostics.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.UI.Tray.Fakes;

namespace Bosun.Tests.Diagnostics;

/// <summary>
/// bs-egu: the bundle carries rclone's own list of mounts (to compare with <c>drives.json</c>) and the
/// bootstrap failure log. Fakes for rclone and every machine seam; the bootstrap log is a file in a temp
/// directory this test owns, never the real <c>%TEMP%\Bosun</c>.
/// </summary>
public sealed class DiagnosticsBundleRcloneMountsAndBootstrapTests : IDisposable
{
    private const string PlantedPassword = "PlantedPw+/Zq9=Secret";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bosun-egu-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _logs;
    private readonly string _bootstrapLog;

    private readonly FakeRcloneClient _rclone = new();
    private readonly FakeDriveLister _drives = new();
    private string? _bootstrapPath;
    private int _tailBytes = 64 * 1024;

    public DiagnosticsBundleRcloneMountsAndBootstrapTests()
    {
        _logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logs);
        File.WriteAllText(Path.Combine(_root, "hosts.toml"), "[global]\nrclone_rc_port = 5572\n");
        _bootstrapLog = Path.Combine(_root, "temp", "Bosun", "bootstrap-failures.log");
        _bootstrapPath = _bootstrapLog;
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
            ConfigPath = Path.Combine(_root, "hosts.toml"),
            RcPort = () => 5572,
            BootstrapFailuresLogPath = _bootstrapPath,
            BootstrapFailuresLogTailBytes = _tailBytes,
        },
        new FakeAppHealth(AppHealth.Healthy),
        new FakeMountSupervisor(),
        _rclone,
        new FakePortOwnerResolver(),
        new FakeProcessInspector(),
        new FakeProcessLister(),
        _drives,
        new FakeWindowsEventSource(),
        new FakeEnvironmentInfoSource(),
        new RcloneRcCredential("planted-user-7f3a", PlantedPassword),
        new FakeTimeProvider(Now));

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

    private async Task<(DiagnosticsBundleResult Result, Dictionary<string, string> Files)> Build()
    {
        var result = await CreateBuilder().BuildAsync();
        return (result, ReadZip(result.Path));
    }

    private void WriteBootstrapLog(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_bootstrapLog)!);
        File.WriteAllText(_bootstrapLog, content);
    }

    // ---- rclone's own mounts ----------------------------------------------------------------

    [Fact]
    public async Task Rclones_mount_list_is_written_and_a_mount_that_is_not_a_logical_drive_is_flagged()
    {
        _rclone.SeedMount(new RcloneMountInfo { Fs = "example-nas:", MountPoint = "P:", MountedOn = Now.AddHours(-2) });
        _rclone.SeedMount(new RcloneMountInfo { Fs = "example-box:", MountPoint = "Q:", MountedOn = Now.AddHours(-1) });
        _drives.Drives.Add(new LogicalDriveEntry(@"C:\", "Fixed"));
        _drives.Drives.Add(new LogicalDriveEntry(@"P:\", "Network"));

        var (result, files) = await Build();

        Assert.Equal(1, _rclone.ListMountsCalls);
        Assert.Contains("\"fs\": \"example-nas:\"", files["rclone-mounts.json"]);
        Assert.Contains("\"mountPoint\": \"Q:\"", files["rclone-mounts.json"]);
        Assert.Contains("\"unavailable\": null", files["rclone-mounts.json"]);
        Assert.Contains("RCLONE MOUNTS (2)", files["summary.txt"]);
        Assert.Contains("P: <- example-nas:", files["summary.txt"]);
        var qLine = files["summary.txt"].Split('\n').Single(l => l.Contains("Q: <- example-box:", StringComparison.Ordinal));
        Assert.Contains("NOT among the logical drives", qLine);
        var pLine = files["summary.txt"].Split('\n').Single(l => l.Contains("P: <- example-nas:", StringComparison.Ordinal));
        Assert.DoesNotContain("NOT among", pLine);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task An_rcd_with_no_mounts_is_an_empty_list_not_an_unavailable_one()
    {
        var (_, files) = await Build();

        Assert.Contains("\"mounts\": []", files["rclone-mounts.json"]);
        Assert.Contains("RCLONE MOUNTS (0)", files["summary.txt"]);
    }

    [Fact]
    public async Task When_rcd_does_not_answer_core_version_listmounts_is_not_asked_and_that_is_not_a_problem()
    {
        _rclone.EnqueueVersionFailure(new HttpRequestException("connection refused"));

        var (result, files) = await Build();

        Assert.Equal(0, _rclone.ListMountsCalls);
        Assert.Contains("unavailable: rclone rc is not answering (HttpRequestException: connection refused)", files["rclone-mounts.json"]);
        Assert.Contains("RCLONE MOUNTS: unavailable: rclone rc is not answering", files["summary.txt"]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task A_listmounts_failure_after_rcd_answered_is_recorded_as_a_collection_problem()
    {
        _rclone.ListMountsThrows = new HttpRequestException("500 from rcd");

        var (result, files) = await Build();

        var problem = Assert.Single(result.Problems);
        Assert.Contains("mount/listmounts failed: HttpRequestException: 500 from rcd", problem);
        Assert.Contains("unavailable: mount/listmounts failed", files["rclone-mounts.json"]);
        Assert.Contains("rclone: v1.71.2", files["version.txt"]);
        Assert.True(files.ContainsKey("drives.json"));
    }

    [Fact]
    public async Task The_mount_list_is_redacted_like_every_other_entry()
    {
        _rclone.SeedMount(new RcloneMountInfo { Fs = $"sftp://planted-user-7f3a:{PlantedPassword}@files.example.net/", MountPoint = "P:" });

        var (_, files) = await Build();

        Assert.DoesNotContain(files.Values, text => text.Contains(PlantedPassword, StringComparison.Ordinal));
        Assert.Contains("files.example.net", files["rclone-mounts.json"]);
    }

    // ---- bootstrap-failures.log -------------------------------------------------------------

    [Fact]
    public async Task The_bootstrap_failure_log_is_included_when_it_exists()
    {
        WriteBootstrapLog("2026-11-01T14:00:00Z The host failed to build.\nSystem.InvalidOperationException: boom\n");

        var (result, files) = await Build();

        Assert.Contains("The host failed to build.", files["bootstrap-failures.log"]);
        Assert.Contains("BOOTSTRAP FAILURES: bootstrap-failures.log,", files["summary.txt"]);
        Assert.Contains("  bootstrap-failures.log" + Environment.NewLine, files["summary.txt"]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task No_bootstrap_failure_log_means_no_entry_and_no_problem()
    {
        var (result, files) = await Build();

        Assert.DoesNotContain("bootstrap-failures.log", files.Keys);
        Assert.Contains("BOOTSTRAP FAILURES: none recorded", files["summary.txt"]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task No_configured_path_is_treated_like_no_file()
    {
        _bootstrapPath = null;

        var (result, files) = await Build();

        Assert.DoesNotContain("bootstrap-failures.log", files.Keys);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task A_large_bootstrap_log_keeps_only_the_end_starting_at_a_whole_line()
    {
        _tailBytes = 200;
        var lines = Enumerable.Range(1, 50).Select(n => $"failure number {n:D2} with some padding text");
        WriteBootstrapLog(string.Join("\n", lines) + "\n");

        var (_, files) = await Build();

        var text = files["bootstrap-failures.log"];
        Assert.StartsWith("[Only the last 200 of ", text, StringComparison.Ordinal);
        Assert.Contains("failure number 50 with some padding text", text);
        Assert.DoesNotContain("failure number 01", text, StringComparison.Ordinal);
        // The first kept line is a whole one: no fragment of a line the cut landed in.
        var firstKept = text.Split('\n')[1];
        Assert.StartsWith("failure number ", firstKept, StringComparison.Ordinal);
        Assert.Contains("only the end is included", files["summary.txt"]);
    }

    [Fact]
    public async Task The_bootstrap_log_is_copied_while_the_sink_still_holds_it_open_for_appending()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_bootstrapLog)!);
        using var writer = new FileStream(_bootstrapLog, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Encoding.UTF8.GetBytes("a failure still being written\n"));
        writer.Flush();

        var (_, files) = await Build();

        Assert.Contains("a failure still being written", files["bootstrap-failures.log"]);
    }

    [Fact]
    public async Task The_bootstrap_log_is_redacted()
    {
        WriteBootstrapLog($"rclone rcd failed: --rc-pass={PlantedPassword} rejected\npassword = \"hunter2\"\n");

        var (_, files) = await Build();

        Assert.DoesNotContain(files.Values, text => text.Contains(PlantedPassword, StringComparison.Ordinal));
        Assert.Contains("--rc-pass=***", files["bootstrap-failures.log"]);
    }

    [Fact]
    public async Task A_bootstrap_log_that_cannot_be_read_is_a_problem_and_the_bundle_still_completes()
    {
        // The file exists but another handle holds it exclusively, so opening it fails.
        Directory.CreateDirectory(Path.GetDirectoryName(_bootstrapLog)!);
        using var exclusive = new FileStream(_bootstrapLog, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var (result, files) = await Build();

        Assert.Single(result.Problems, p => p.StartsWith("bootstrap-failures.log could not be collected", StringComparison.Ordinal));
        Assert.True(files.ContainsKey("summary.txt"));
        Assert.True(files.ContainsKey("rclone-mounts.json"));
        Assert.Contains("BOOTSTRAP FAILURES: unavailable", files["summary.txt"]);
    }
}
