using System.IO.Compression;
using Bosun.Diagnostics;
using Bosun.Health;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.SessionMonitor.Interop;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.UI.Tray.Fakes;

namespace Bosun.Tests.Diagnostics;

/// <summary>
/// Opt-in: builds a bundle from the REAL process table, CIM, Event Log, drive list and assembly
/// metadata, to check the real sources work on a real machine (run with
/// <c>--settings tests/Bosun.Tests/integration.runsettings</c>). Read-only throughout: the
/// supervisor and rclone client are fakes, nothing is mounted, no drive is queried beyond its type,
/// Explorer is not launched, and the output goes to a temp directory.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DiagnosticsBundleIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bosun-ds3-it-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task A_bundle_built_from_the_real_machine_has_every_section_and_no_collection_problems()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "bosun-test.log"), "a line\n");
        var config = Path.Combine(_root, "hosts.toml");
        File.WriteAllText(config, "[global]\nrclone_rc_port = 5572\n");

        var builder = new DiagnosticsBundleBuilder(
            new DiagnosticsBundleOptions
            {
                OutputDirectory = Path.Combine(_root, "diagnostics"),
                LogDirectory = logs,
                ConfigPath = config,
                RcPort = () => 5572,
            },
            new TestHealth(),
            new FakeMountSupervisor(),
            new FakeRcloneClient(),
            new TcpPortOwnerResolver(new Win32TcpConnectionReader()),
            new CimProcessInspector(),
            new SystemProcessLister(),
            new SystemDriveLister(),
            new ApplicationEventLogSource(),
            new AssemblyEnvironmentInfoSource(),
            RcloneRcCredential.CreateRandom(),
            TimeProvider.System);

        var result = await builder.BuildAsync();

        Assert.Empty(result.Problems);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Contains(zip.Entries, e => e.FullName == "drives.json");
        using var drives = new StreamReader(zip.GetEntry("drives.json")!.Open());
        Assert.Contains("Fixed", drives.ReadToEnd());
        using var version = new StreamReader(zip.GetEntry("version.txt")!.Open());
        Assert.Contains("Bosun assembly version:", version.ReadToEnd());
    }

    private sealed class TestHealth : IAppHealth
    {
        public AppHealth Current => AppHealth.Healthy;

        public event EventHandler<AppHealthChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }
}
