using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Bosun.Rclone.Process;

/// <summary>
/// The seam over a Win32 Job Object (ADR-020 §1, bs-772). A child assigned to the job is killed by
/// the OS when the job's last handle closes, which for the production implementation happens only
/// when Bosun itself exits for ANY reason (crash, AppHang kill, Task Manager). That is what stops
/// <c>rclone rcd</c> surviving its parent and holding the rc port with a dead instance's credential.
/// </summary>
public interface IProcessJob
{
    /// <summary>
    /// Assigns the process to the job. Returns <see langword="false"/> (with a reason in
    /// <paramref name="error"/>) rather than throwing when the OS refuses; the caller logs and
    /// carries on, because a failed assignment degrades crash-cleanup but must not stop startup.
    /// </summary>
    bool TryAssign(SafeProcessHandle process, out string error);
}

/// <summary>
/// The "assign, and on failure log a Warning but do not throw" policy, pulled out of
/// <see cref="Win32RcloneProcessLauncher"/> so it can be unit-tested with a fake
/// <see cref="IProcessJob"/> and no real process (CLAUDE.md worktree-safety rules).
/// </summary>
internal static class JobAssignment
{
    public static bool TryAssignOrWarn(IProcessJob job, SafeProcessHandle process, string executable, ILogger logger)
    {
        string error;
        try
        {
            if (job.TryAssign(process, out error))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }

        logger.LogWarning(
            "Could not assign {Executable} to Bosun's Job Object ({Reason}). Startup continues, but if Bosun " +
            "is killed abruptly this process will NOT be cleaned up by the OS; the next launch will detect " +
            "and replace it instead (ADR-020 §2).",
            executable, error);
        return false;
    }
}
