using System.Runtime.InteropServices;
using Bosun.Rclone.Process;
using Bosun.Rclone.Process.Interop;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.Supervisor.Independent.RcTimeout;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// Default-suite coverage of the Job Object policy (ADR-020 §1, bs-772). A fake
/// <see cref="IProcessJob"/> stands in: no real Job Object is created and no process is touched.
/// Whether the real job actually kills a child is the opt-in
/// <c>Win32ProcessJobIntegrationTests</c>.
/// </summary>
public sealed class JobObjectTests
{
    [Fact]
    public void A_successful_assignment_logs_nothing()
    {
        var job = new FakeProcessJob();
        var logger = new LevelRecordingLogger<Win32RcloneProcessLauncher>();

        var assigned = JobAssignment.TryAssignOrWarn(job, new SafeProcessHandle(), "rclone", logger);

        Assert.True(assigned);
        Assert.Equal(1, job.AssignCalls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_refused_assignment_logs_a_Warning_with_the_reason_and_does_not_throw()
    {
        var job = new FakeProcessJob { Succeeds = false };
        var logger = new LevelRecordingLogger<Win32RcloneProcessLauncher>();

        var assigned = JobAssignment.TryAssignOrWarn(job, new SafeProcessHandle(), "rclone", logger);

        Assert.False(assigned);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("access is denied (fake)", entry.Message);
        Assert.Contains("rclone", entry.Message);
    }

    [Fact]
    public void A_job_that_throws_is_also_only_a_Warning()
    {
        var job = new FakeProcessJob { Throws = new InvalidOperationException("job exploded (fake)") };
        var logger = new LevelRecordingLogger<Win32RcloneProcessLauncher>();

        var assigned = JobAssignment.TryAssignOrWarn(job, new SafeProcessHandle(), "rclone", logger);

        Assert.False(assigned);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("job exploded (fake)", entry.Message);
    }

    [Fact]
    public void The_extended_limit_struct_has_the_native_x64_size()
    {
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on x64. A wrong managed layout makes
        // SetInformationJobObject fail, or applies garbage flags -- and then the kill-on-close
        // guarantee silently does not exist.
        if (IntPtr.Size != 8)
        {
            return;
        }

        Assert.Equal(144, Marshal.SizeOf<Win32ProcessJob.JobObjectExtendedLimitInformation>());
        Assert.Equal(64, Marshal.SizeOf<Win32ProcessJob.JobObjectBasicLimitInformation>());
        Assert.Equal(48, Marshal.SizeOf<Win32ProcessJob.IoCounters>());
    }
}
