namespace Bosun.Rclone.Process;

/// <summary>
/// The one operation the repair actions need from <see cref="RcloneProcessService"/> (bs-aoz):
/// replace the running <c>rclone rcd</c> with a fresh one. Behind an interface so the repair
/// command's tests never start a process; <see cref="RcloneProcessService"/> is the only
/// implementation.
/// </summary>
public interface IRcloneProcessRestarter
{
    /// <summary>
    /// Stops the current rcd and starts a fresh one through the service's ordinary launch path.
    /// Returns <see langword="true"/> if the new process became healthy. Never throws for an
    /// ordinary failure to start; see <see cref="RcloneProcessService.RestartAsync"/>.
    /// </summary>
    Task<bool> RestartAsync(CancellationToken cancellationToken = default);
}
