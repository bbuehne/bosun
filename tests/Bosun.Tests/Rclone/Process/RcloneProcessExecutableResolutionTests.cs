using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// bs-64k at the service level: <see cref="RcloneProcessService"/> launches the path the resolver
/// found, re-resolves on every start attempt, and when nothing is found reports the places
/// searched without launching anything. Fakes only: no process, no PATH, no filesystem.
/// </summary>
public sealed class RcloneProcessExecutableResolutionTests
{
    private const string Found = @"C:\Users\test\AppData\Local\rclone\rclone.exe";

    [Fact]
    public async Task The_launcher_receives_the_path_the_resolver_found()
    {
        var f = new Fixture();
        f.Resolver.Enqueue(Found, "%LOCALAPPDATA%\\rclone");

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        Assert.Equal(Found, Assert.Single(f.Launcher.StartCalls).ExecutablePath);
        Assert.Equal(["rclone"], f.Resolver.ResolveCalls);
    }

    [Fact]
    public async Task The_log_says_which_location_was_used()
    {
        var f = new Fixture();
        f.Resolver.Enqueue(Found, "%LOCALAPPDATA%\\rclone");

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Contains(f.Log.CapturedText, line => line.Contains(Found, StringComparison.Ordinal));
        Assert.Contains(f.Log.CapturedText, line => line.Contains("%LOCALAPPDATA%\\rclone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_found_faults_with_a_message_listing_where_it_looked_and_never_launches()
    {
        var f = new Fixture();
        f.Resolver.EnqueueNotFound(["PATH", @"C:\a\rclone.exe", @"C:\b\rclone.exe"]);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, f.Service.FaultKind);
        Assert.Empty(f.Launcher.StartCalls);
        Assert.Contains("PATH", f.Service.LastFaultMessage);
        Assert.Contains(@"C:\a\rclone.exe", f.Service.LastFaultMessage);
        Assert.Contains(@"C:\b\rclone.exe", f.Service.LastFaultMessage);
        Assert.Contains("restart Bosun", f.Service.LastFaultMessage);
    }

    [Fact]
    public async Task Nothing_found_is_terminal_and_is_not_retried()
    {
        var f = new Fixture();
        f.Resolver.EnqueueNotFound(["PATH"]);

        await f.Service.StartAsync(CancellationToken.None);
        f.Time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();

        Assert.Single(f.Resolver.ResolveCalls);
        Assert.Empty(f.Launcher.StartCalls);
    }

    [Fact]
    public async Task A_restart_looks_again_and_picks_up_an_rclone_installed_in_the_meantime()
    {
        var f = new Fixture();
        f.Resolver.EnqueueNotFound(["PATH"]);
        await f.Service.StartAsync(CancellationToken.None);
        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, f.Service.FaultKind);

        f.Resolver.Enqueue(Found, "%LOCALAPPDATA%\\rclone");
        var restarted = await f.Service.RestartAsync();

        Assert.True(restarted);
        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        Assert.Equal(Found, Assert.Single(f.Launcher.StartCalls).ExecutablePath);
        Assert.Equal(2, f.Resolver.ResolveCalls.Count);
    }

    [Fact]
    public async Task Without_a_resolver_the_configured_name_goes_to_the_launcher_unchanged()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = new RcloneProcessService(
            launcher,
            new FakeRcloneClient(),
            new RcloneProcessServiceOptions { RcloneRcPort = 5572, RcloneConfigPath = @"C:\fixture\rclone.conf" },
            new FakeTimeProvider(),
            new RecordingLogger<RcloneProcessService>(),
            new RcloneRcCredential("u", "p"));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal("rclone", Assert.Single(launcher.StartCalls).ExecutablePath);
    }

    [Fact]
    public async Task A_launcher_reported_not_found_is_still_handled_as_before()
    {
        var f = new Fixture();
        f.Resolver.Enqueue(Found, "PATH");
        f.Launcher.EnqueueExecutableNotFound();

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, f.Service.FaultKind);
        Assert.Single(f.Launcher.StartCalls);
    }

    private sealed class ScriptedResolver : IRcloneExecutableResolver
    {
        private readonly Queue<RcloneExecutableResolution> _script = new();

        public List<string> ResolveCalls { get; } = [];

        public void Enqueue(string path, string source) =>
            _script.Enqueue(new RcloneExecutableResolution { Path = path, Source = source, Searched = [source] });

        public void EnqueueNotFound(IReadOnlyList<string> searched) =>
            _script.Enqueue(new RcloneExecutableResolution { Searched = searched });

        public RcloneExecutableResolution Resolve(string configuredPath)
        {
            ResolveCalls.Add(configuredPath);
            return _script.Dequeue();
        }
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Service = new RcloneProcessService(
                Launcher,
                new FakeRcloneClient(),
                new RcloneProcessServiceOptions { RcloneRcPort = 5572, RcloneConfigPath = @"C:\fixture\rclone.conf" },
                Time,
                Log,
                new RcloneRcCredential("bosun-test-user", "bosun-test-pass"),
                executableResolver: Resolver);
        }

        public FakeRcloneProcessLauncher Launcher { get; } = new();
        public ScriptedResolver Resolver { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingLogger<RcloneProcessService> Log { get; } = new();
        public RcloneProcessService Service { get; }
    }
}
