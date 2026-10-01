using Bosun.Rclone.Process;
using Bosun.Rclone.Process.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// Opt-in tests against REAL Win32 objects (excluded from the default suite; run with
/// <c>--settings tests/Bosun.Tests/integration.runsettings</c>). They only ever act on processes
/// the test itself started (<c>ping.exe</c>, a harmless 20-second loopback ping). No rclone, no
/// WinFsp, no drive letter, and nothing is aimed at any process the test did not create.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class Win32ProcessJobIntegrationTests
{
    private static RcloneProcessStartInfo LongPing() => new()
    {
        ExecutablePath = "ping.exe",
        Arguments = ["-n", "20", "127.0.0.1"],
    };

    [Fact]
    public async Task Disposing_the_job_kills_a_child_that_was_assigned_to_it()
    {
        using var job = new Win32ProcessJob();
        var launcher = new Win32RcloneProcessLauncher(job, NullLogger<Win32RcloneProcessLauncher>.Instance);
        using var handle = launcher.Start(LongPing());

        var exited = new TaskCompletionSource();
        handle.Exited += (_, _) => exited.TrySetResult();
        try
        {
            Assert.False(handle.HasExited, "the child should be running before the job is closed");

            job.Dispose(); // what the OS does when Bosun dies: the last job handle closes

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(handle.HasExited);
        }
        finally
        {
            handle.Kill(); // never leave the test's own child behind, whatever happened
        }
    }

    [Fact]
    public void A_child_that_was_never_assigned_survives_the_job_closing()
    {
        // The control for the test above: proves it is the job, not something incidental, that
        // killed the child.
        using var job = new Win32ProcessJob();
        var launcher = new Win32RcloneProcessLauncher(); // no job
        using var handle = launcher.Start(LongPing());
        try
        {
            job.Dispose();
            Thread.Sleep(500);
            Assert.False(handle.HasExited);
        }
        finally
        {
            handle.Kill();
        }
    }

    [Fact]
    public void Assigning_an_invalid_handle_reports_failure_instead_of_throwing()
    {
        using var job = new Win32ProcessJob();

        var assigned = job.TryAssign(new Microsoft.Win32.SafeHandles.SafeProcessHandle(), out var error);

        Assert.False(assigned);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
