using Bosun.Supervisor;

namespace Bosun.Tests.Status.Fakes;

/// <summary>
/// Scriptable <see cref="IMountSupervisor"/> for <c>StatusReadModel</c> tests -- only
/// <see cref="GetSnapshot"/> and <see cref="GetTransitionHistory"/> are ever called by the class
/// under test; every other member throws if called, following the same trip-wire pattern as
/// <c>SystemEventIntegration.Fakes.FakeMountSupervisor</c>.
/// </summary>
internal sealed class FakeMountSupervisorSnapshotSource : IMountSupervisor
{
    public IReadOnlyList<HostMountSnapshot> SnapshotToReturn { get; set; } = [];

    public IReadOnlyList<MountTransitionEntry> TransitionHistoryToReturn { get; set; } = [];

    public int GetSnapshotCallCount { get; private set; }

    public IReadOnlyList<HostMountSnapshot> GetSnapshot()
    {
        GetSnapshotCallCount++;
        return SnapshotToReturn;
    }

    public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() => TransitionHistoryToReturn;

    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SuspendAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ResumeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
