using Bosun.Supervisor;

namespace Bosun.Tests.UI.Tray.Fakes;

/// <summary>Records every call instead of driving a real state machine -- these tests exercise UI
/// glue (dispatch, notification detection), not the supervisor itself (that is
/// <c>tests/Bosun.Tests/Supervisor</c>'s job).</summary>
internal sealed class FakeMountSupervisor : IMountSupervisor
{
    public List<string> MountRequests { get; } = [];
    public List<string> UnmountRequests { get; } = [];
    public List<string> RetryNowRequests { get; } = [];
    public List<string> ActivityRequests { get; } = [];

    public IReadOnlyList<HostMountSnapshot> Snapshot { get; set; } = [];
    public IReadOnlyList<MountTransitionEntry> TransitionHistory { get; set; } = [];

    public Exception? MountRequestException { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<HostMountSnapshot> GetSnapshot() => Snapshot;

    public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() => TransitionHistory;

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        MountRequests.Add(hostKey);
        return MountRequestException is not null ? Task.FromException(MountRequestException) : Task.CompletedTask;
    }

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        UnmountRequests.Add(hostKey);
        return Task.CompletedTask;
    }

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        RetryNowRequests.Add(hostKey);
        return Task.CompletedTask;
    }

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        ActivityRequests.Add(hostKey);
        return Task.CompletedTask;
    }

    public Task SuspendAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
