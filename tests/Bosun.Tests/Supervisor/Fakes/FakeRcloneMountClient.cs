using Bosun.Rclone;

namespace Bosun.Tests.Supervisor.Fakes;

/// <summary>
/// Scriptable <see cref="IRcloneClient"/> for <c>MountSupervisor</c> tests, focused on the three
/// calls only <c>IMountSupervisor</c> is allowed to make (<see cref="MountAsync"/>,
/// <see cref="UnmountAsync"/>, <see cref="ListMountsAsync"/>) plus the ones the state machine
/// depends on for correctness proofs. Maintains an in-memory mount table so
/// <see cref="ListMountsAsync"/> genuinely reflects prior <see cref="MountAsync"/>/
/// <see cref="UnmountAsync"/> calls -- this is what lets tests prove
/// <c>AttemptDrainStepAsync</c>'s "verify against listmounts, never assume" behaviour rather than
/// just trusting the unmount call's own return.
/// </summary>
internal sealed class FakeRcloneMountClient : IRcloneClient
{
    private readonly List<RcloneMountInfo> mounts = [];
    private readonly Dictionary<string, Exception> mountFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> unmountCallCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> confirmUnmountAfterCall = new(StringComparer.OrdinalIgnoreCase);
    private Exception? listMountsThrowsOnce;

    public List<RcloneMountRequest> MountCalls { get; } = [];
    public List<string> UnmountCalls { get; } = [];
    public int ListMountsCallCount { get; private set; }

    /// <summary>Seeds the fake as if a mount already existed before the test started -- for
    /// startup adopt-or-clear and reconciliation-drift tests.</summary>
    public void SeedExistingMount(string mountPoint, string fs) =>
        mounts.Add(new RcloneMountInfo { Fs = fs, MountPoint = mountPoint, MountedOn = DateTimeOffset.UnixEpoch });

    /// <summary>Removes a mount from the fake's table without going through
    /// <see cref="UnmountAsync"/> -- simulates rclone silently losing a mount out from under
    /// Bosun, for reconciliation-drift tests.</summary>
    public void SimulateMountLost(string mountPoint) =>
        mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

    public void MakeMountFail(string mountPoint, Exception exception) => mountFailures[mountPoint] = exception;

    /// <summary>The unmount call at <paramref name="mountPoint"/> only actually clears the fake's
    /// mount table on its <paramref name="callNumber"/>-th invocation (1-based). Use a large value
    /// to simulate "never confirms"; use 2 to simulate "the clean attempt does not confirm but the
    /// forced-escalation retry does".</summary>
    public void ConfirmUnmountAfterCall(string mountPoint, int callNumber) => confirmUnmountAfterCall[mountPoint] = callNumber;

    public void MakeListMountsThrowOnce(Exception exception) => listMountsThrowsOnce = exception;

    public int UnmountCallCountFor(string mountPoint) =>
        unmountCallCounts.TryGetValue(mountPoint, out var count) ? count : 0;

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RcloneVersionInfo { Version = "v1.71.2" });

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        MountCalls.Add(request);

        if (mountFailures.Remove(request.MountPoint, out var exception))
        {
            return Task.FromException<RcloneMountResult>(exception);
        }

        mounts.RemoveAll(m => string.Equals(m.MountPoint, request.MountPoint, StringComparison.OrdinalIgnoreCase));
        mounts.Add(new RcloneMountInfo { Fs = request.Fs, MountPoint = request.MountPoint, MountedOn = DateTimeOffset.UnixEpoch });
        return Task.FromResult(new RcloneMountResult { MountPoint = request.MountPoint });
    }

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        UnmountCalls.Add(mountPoint);

        var count = unmountCallCounts.TryGetValue(mountPoint, out var existing) ? existing + 1 : 1;
        unmountCallCounts[mountPoint] = count;

        var confirmAt = confirmUnmountAfterCall.TryGetValue(mountPoint, out var n) ? n : 1;
        if (count >= confirmAt)
        {
            mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        ListMountsCallCount++;

        if (listMountsThrowsOnce is not null)
        {
            var exception = listMountsThrowsOnce;
            listMountsThrowsOnce = null;
            return Task.FromException<IReadOnlyList<RcloneMountInfo>>(exception);
        }

        return Task.FromResult<IReadOnlyList<RcloneMountInfo>>(mounts.ToList());
    }

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RcloneListItem>>([]);
}
