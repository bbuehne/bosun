using Bosun.Supervisor;

namespace Bosun.Tests.SystemEventIntegration.Fakes;

/// <summary>
/// Scriptable <see cref="IMountSupervisor"/> for <c>SystemEventSupervisorAdapter</c> tests. The
/// adapter (bs-ohk) only ever calls <see cref="SuspendAsync"/>, <see cref="ResumeAsync"/>, and
/// <see cref="NetworkChangedAsync"/> -- every other member exists only to satisfy the interface and
/// throws if called, so a test that accidentally exercises the wrong path fails loudly instead of
/// silently no-op'ing.
/// </summary>
internal sealed class FakeMountSupervisor : IMountSupervisor
{
    public List<string> SuspendCalls { get; } = [];
    public List<string> ResumeCalls { get; } = [];
    public List<string> NetworkChangedCalls { get; } = [];

    /// <summary>When set, <see cref="SuspendAsync"/> returns this task instead of completing
    /// immediately -- the seam that lets a test simulate a busy command channel / an in-flight
    /// drain elsewhere, without needing a real <c>MountSupervisor</c> or real time.</summary>
    public TaskCompletionSource? PendingSuspend { get; set; }

    public Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        SuspendCalls.Add("suspend");
        return PendingSuspend?.Task ?? Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ResumeCalls.Add("resume");
        return Task.CompletedTask;
    }

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default)
    {
        NetworkChangedCalls.Add("network-changed");
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IReadOnlyList<HostMountSnapshot> GetSnapshot() => throw new NotSupportedException();

    public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() => throw new NotSupportedException();

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ConfigChangedAsync(Bosun.Configuration.BosunConfig newConfig, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
