using Bosun.Configuration;

namespace Bosun.Supervisor;

/// <summary>
/// An <see cref="IMountSupervisor"/> that supervises nothing, safely (bs-4js). Stands in for the real
/// one when the container cannot build it -- the exact fault bs-4js was filed for: an unregistered
/// <see cref="TimeProvider"/> made <c>IProbe</c>, and therefore <c>MountSupervisor</c>, impossible to
/// construct, and the UI that needed the supervisor died with it. The tray icon is the channel that
/// reports "the supervisor failed", so it must be buildable when the supervisor is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is a deliberate no-op that never throws.</b> The snapshot and the transition
/// history are empty, so the status window and tray show no host rows and therefore offer no
/// mount/unmount items to click; the commands exist only so a caller that still holds the interface
/// (the host editor's delete path, the repair actions) degrades to "nothing to do" instead of an
/// exception on a UI thread. That is also correct, not merely safe: a supervisor that never existed
/// mounted nothing in this process, so there is nothing to unmount.
/// </para>
/// <para>
/// <b>It never calls rclone.</b> It holds no <c>IRcloneClient</c>, so it cannot issue
/// <c>mount/mount</c> or <c>mount/unmount</c>; the "only <see cref="IMountSupervisor"/> mounts"
/// boundary is untouched, and no mount state machine is involved.
/// </para>
/// <para>
/// The cause is NOT carried here. It reaches the user through the app-health model
/// (<c>StartupReadiness.SupervisorFaultMessage</c> -&gt; <c>startup.supervisor-failed</c>), the same
/// path whether or not the UI could resolve the real supervisor, so there is one source of truth.
/// </para>
/// </remarks>
public sealed class UnavailableMountSupervisor : IMountSupervisor
{
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<HostMountSnapshot> GetSnapshot() => [];

    public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() => [];

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SuspendAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RepairAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ConfigChangedAsync(BosunConfig newConfig, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
