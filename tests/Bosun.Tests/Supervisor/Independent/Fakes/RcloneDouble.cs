using Bosun.Rclone;

namespace Bosun.Tests.Supervisor.Independent.Fakes;

/// <summary>
/// An <see cref="IRcloneClient"/> double written independently of the implementer's
/// <c>FakeRcloneMountClient</c>, with a real in-memory mount table so
/// <see cref="ListMountsAsync"/> reflects reality rather than intent.
/// </summary>
/// <remarks>
/// <para>
/// Additions over the implementer's fake, each required by a test in this folder:
/// <see cref="ListMountsAlwaysFails"/> (a persistently unreachable <c>rclone rcd</c>, not a
/// single blip -- docs/ARCHITECTURE.md §4 rule 4's "never leave the state machine believing a
/// drive is gone when it is not" is only really tested when the machine can NEVER confirm);
/// bounded mount failures (so an unbounded-retry bug fails an assertion instead of hanging the
/// run); and per-mount-point call counts plus shared-log ordering.
/// </para>
/// <para>
/// This double never touches a real drive letter, a real <c>rclone rcd</c>, or WinFsp -- it is a
/// dictionary and a list. No test in this folder is Integration-category because none of them can
/// reach a real system.
/// </para>
/// </remarks>
internal sealed class RcloneDouble(SupervisorCallLog log) : IRcloneClient
{
    private readonly List<RcloneMountInfo> mounts = [];
    private readonly Dictionary<string, int> unmountCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> mountCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> confirmUnmountAtCall = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingFailure> mountFailures = new(StringComparer.OrdinalIgnoreCase);

    public List<RcloneMountRequest> MountRequests { get; } = [];

    public int ListMountsCalls { get; private set; }

    /// <summary>When true, every <see cref="ListMountsAsync"/> throws -- an <c>rclone rcd</c> that
    /// is down for good, not for one call.</summary>
    public bool ListMountsAlwaysFails { get; set; }

    public int TotalMountCalls => mountCounts.Values.Sum();

    public int MountCount(string mountPoint) => mountCounts.TryGetValue(mountPoint, out var n) ? n : 0;

    public int UnmountCount(string mountPoint) => unmountCounts.TryGetValue(mountPoint, out var n) ? n : 0;

    /// <summary>The rclone fs actually served at <paramref name="mountPoint"/> right now, or
    /// <see langword="null"/> if nothing is mounted there. This is what a user's Explorer window
    /// would be showing -- distinct from whatever the supervisor believes.</summary>
    public string? FsAt(string mountPoint) =>
        mounts.FirstOrDefault(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase))?.Fs;

    /// <summary>Seeds the table as if the mount already existed before the supervisor started --
    /// crash recovery (docs/ARCHITECTURE.md §6) and out-of-band drift.</summary>
    public void SeedMount(string mountPoint, string fs) =>
        mounts.Add(new RcloneMountInfo { Fs = fs, MountPoint = mountPoint, MountedOn = DateTimeOffset.UnixEpoch });

    /// <summary>Removes a mount without an <see cref="UnmountAsync"/> call -- rclone losing a
    /// mount out from under Bosun.</summary>
    public void DropMountSilently(string mountPoint) =>
        mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

    /// <summary>The mount at <paramref name="mountPoint"/> only actually disappears on the
    /// <paramref name="callNumber"/>-th <see cref="UnmountAsync"/> (1-based). <c>int.MaxValue</c>
    /// means "the unmount never takes effect".</summary>
    public void ConfirmUnmountAtCall(string mountPoint, int callNumber) =>
        confirmUnmountAtCall[mountPoint] = callNumber;

    /// <summary>The next <paramref name="times"/> mount attempts at <paramref name="mountPoint"/>
    /// throw. Bounded on purpose -- see the class remarks.</summary>
    public void FailMount(string mountPoint, Exception exception, int times) =>
        mountFailures[mountPoint] = new PendingFailure(exception, times);

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RcloneVersionInfo { Version = "v1.71.2-double" });

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        MountRequests.Add(request);
        mountCounts[request.MountPoint] = MountCount(request.MountPoint) + 1;

        if (mountFailures.TryGetValue(request.MountPoint, out var failure) && failure.TryConsume(out var exception))
        {
            log.Record($"mount|{request.MountPoint}|failed");
            return Task.FromException<RcloneMountResult>(exception);
        }

        log.Record($"mount|{request.MountPoint}");
        mounts.RemoveAll(m => string.Equals(m.MountPoint, request.MountPoint, StringComparison.OrdinalIgnoreCase));
        mounts.Add(new RcloneMountInfo
        {
            Fs = request.Fs,
            MountPoint = request.MountPoint,
            MountedOn = DateTimeOffset.UnixEpoch,
        });

        return Task.FromResult(new RcloneMountResult { MountPoint = request.MountPoint });
    }

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        var count = UnmountCount(mountPoint) + 1;
        unmountCounts[mountPoint] = count;
        log.Record($"unmount|{mountPoint}");

        var confirmAt = confirmUnmountAtCall.TryGetValue(mountPoint, out var n) ? n : 1;
        if (count >= confirmAt)
        {
            mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        ListMountsCalls++;

        if (ListMountsAlwaysFails)
        {
            log.Record("listmounts|failed");
            return Task.FromException<IReadOnlyList<RcloneMountInfo>>(
                new InvalidOperationException("rclone rcd is not answering"));
        }

        log.Record($"listmounts|[{string.Join(',', mounts.Select(m => m.MountPoint))}]");
        return Task.FromResult<IReadOnlyList<RcloneMountInfo>>(mounts.ToList());
    }

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RcloneListItem>>([]);

    private sealed class PendingFailure(Exception exception, int times)
    {
        private int remaining = times;

        public bool TryConsume(out Exception value)
        {
            value = exception;
            if (remaining <= 0)
            {
                return false;
            }

            remaining--;
            return true;
        }
    }
}
