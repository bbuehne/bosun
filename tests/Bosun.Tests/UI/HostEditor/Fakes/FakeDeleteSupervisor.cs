using Bosun.Supervisor;

namespace Bosun.Tests.UI.HostEditor.Fakes;

/// <summary>
/// An <see cref="IMountSupervisor"/> that records unmount requests and reports whatever mount
/// state a test wants, so <c>HostEditorController.DeleteAsync</c>'s drain-before-delete behaviour
/// (Invariant I2) can be exercised without a real supervisor.
/// </summary>
internal sealed class FakeDeleteSupervisor : IMountSupervisor
{
    private readonly List<HostMountSnapshot> _snapshot = [];

    public List<string> UnmountRequests { get; } = [];

    /// <summary>When set, <see cref="RequestUnmountAsync"/> throws it -- for the case where a host
    /// cannot be unmounted and the delete must therefore be abandoned.</summary>
    public Exception? UnmountThrows { get; set; }

    public void SetHostState(string hostKey, MountState state) =>
        _snapshot.Add(new HostMountSnapshot
        {
            HostKey = hostKey,
            State = state,
            AdministrativelyEnabled = true,
        });

    public IReadOnlyList<HostMountSnapshot> GetSnapshot() => _snapshot;

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        UnmountRequests.Add(hostKey);
        return UnmountThrows is not null ? Task.FromException(UnmountThrows) : Task.CompletedTask;
    }

    // Everything below is unused by the delete path and simply satisfies the interface.
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() => [];

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SuspendAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
