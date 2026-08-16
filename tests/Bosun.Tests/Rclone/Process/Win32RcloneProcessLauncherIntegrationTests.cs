using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// Exercises <see cref="Win32RcloneProcessLauncher"/> against a REAL process -- excluded from
/// the default suite (CLAUDE.md worktree-safety rules: "no test in the default suite may... start
/// a real process"). Deliberately uses <c>cmd.exe</c>, not <c>rclone</c> -- rclone is not
/// installed on the maintainer's machine as of this epic, and these tests only need to prove the
/// launcher's process mechanics (start, detect exit, kill), which any real executable
/// demonstrates equally well without depending on rclone being present. Run deliberately with
/// <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class Win32RcloneProcessLauncherIntegrationTests
{
    [Fact]
    public async Task Start_launches_the_process_and_Exited_fires_when_it_exits_on_its_own()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/c", "exit 0"],
        });

        var exitedTcs = new TaskCompletionSource();
        handle.Exited += (_, _) => exitedTcs.TrySetResult();

        await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(handle.HasExited);
        Assert.Equal(0, handle.ExitCode);
    }

    [Fact]
    public async Task Start_reports_a_nonzero_exit_code()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/c", "exit 7"],
        });

        var exitedTcs = new TaskCompletionSource();
        handle.Exited += (_, _) => exitedTcs.TrySetResult();
        await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, handle.ExitCode);
    }

    [Fact]
    public void Kill_terminates_a_still_running_process()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "cmd.exe",
            // /c "waits" via ping to localhost -- long enough that Kill (not natural exit) is
            // what ends it.
            Arguments = ["/c", "ping", "-n", "30", "127.0.0.1", ">nul"],
        });

        Assert.False(handle.HasExited);

        handle.Kill();

        // Kill is not guaranteed synchronous end-to-end (TerminateProcess is asynchronous at the
        // OS level), but should be observable almost immediately.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!handle.HasExited && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.True(handle.HasExited);
    }

    [Fact]
    public void Start_throws_RcloneExecutableNotFoundException_for_an_executable_that_does_not_exist()
    {
        var launcher = new Win32RcloneProcessLauncher();

        var ex = Assert.Throws<RcloneExecutableNotFoundException>(() => launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "this-executable-definitely-does-not-exist-bosun-e3.exe",
            Arguments = [],
        }));

        Assert.Contains("PATH", ex.Message);
    }
}
