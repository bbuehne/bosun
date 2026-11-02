using Bosun.Rclone;
using Bosun.Tests.Supervisor.Independent.Fakes;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// An <see cref="IRcloneClient"/> double for the bs-obf independent tests, written from
/// docs/ARCHITECTURE.md §4 rule 7 and the rc endpoint table, not from the implementer's fakes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mount table.</b> A real in-memory table, so <c>mount/listmounts</c> reflects what is actually
/// mounted and <c>vfs/list</c> can list exactly the <c>fs</c> strings the supervisor passed to
/// <c>mount/mount</c> (rule 7: "rclone keys a VFS by the <c>fs</c> string of the mount"). A
/// <c>mount/unmount</c> takes effect at once.
/// </para>
/// <para>
/// <b>VFS script, keyed by drive letter.</b> A test does not need to know the <c>fs</c> string the
/// supervisor builds; it scripts the VFS behind a drive. See <see cref="DriveOf"/> for how a name
/// is resolved, and why an unknown one gets a healthy answer rather than an error.
/// </para>
/// <para>
/// <b>Hang mode.</b> With <see cref="HangVfs"/> set, every <c>vfs/*</c> call blocks until the
/// cancellation token it was given is cancelled, and never returns otherwise: an <c>rclone rcd</c>
/// that accepted the connection and stopped answering. Each blocked call releases
/// <see cref="HungEntries"/> once, so a test can wait for it without sleeping.
/// </para>
/// <para>
/// Nothing here touches a real <c>rclone rcd</c>, WinFsp or a drive letter.
/// </para>
/// </remarks>
internal sealed class VfsScriptedRclone(TimeProvider time, SupervisorCallLog log) : IRcloneClient
{
    private readonly object gate = new();
    private readonly List<RcloneMountInfo> mounts = [];
    private readonly Dictionary<string, RcloneVfsStats> statsByDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<RcloneVfsQueueItem>> queueByDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> vfsCalls = [];
    private readonly List<HungCall> hungCalls = [];

    public List<RcloneMountRequest> MountRequests { get; } = [];

    public List<string> UnmountRequests { get; } = [];

    /// <summary>What <c>vfs/stats</c> answers for a drive nobody scripted: a healthy, idle VFS.</summary>
    public RcloneVfsStats DefaultStats { get; set; } = Vfs.Stats();

    /// <summary>Replaces what <c>vfs/list</c> returns. Receives the fs strings currently mounted.
    /// <see langword="null"/>: list exactly those.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>>? VfsListOverride { get; set; }

    public bool FailVfsList { get; set; }

    public bool FailVfsStats { get; set; }

    public bool FailVfsQueue { get; set; }

    /// <summary>How many of the next <c>vfs/stats</c> calls fail before it answers again.</summary>
    public int FailNextVfsStats { get; set; }

    public bool HangVfs { get; set; }

    /// <summary>Released once per <c>vfs/*</c> call that starts hanging.</summary>
    public SemaphoreSlim HungEntries { get; } = new(0);

    public IReadOnlyList<HungCall> HungCalls
    {
        get
        {
            lock (gate)
            {
                return hungCalls.ToList();
            }
        }
    }

    /// <summary>Every <c>vfs/*</c> call, as <c>endpoint|fs</c> (<c>vfs/list|</c> for the list).</summary>
    public IReadOnlyList<string> VfsCalls
    {
        get
        {
            lock (gate)
            {
                return vfsCalls.ToList();
            }
        }
    }

    public int VfsCallCount => VfsCalls.Count;

    public int VfsCallCountFor(string endpoint) =>
        VfsCalls.Count(c => c.StartsWith(endpoint + "|", StringComparison.Ordinal));

    public int MountCount(string drive) =>
        MountRequests.Count(r => string.Equals(r.MountPoint, drive, StringComparison.OrdinalIgnoreCase));

    public int UnmountCount(string drive) =>
        UnmountRequests.Count(r => string.Equals(r, drive, StringComparison.OrdinalIgnoreCase));

    public bool IsMounted(string drive)
    {
        lock (gate)
        {
            return mounts.Any(m => string.Equals(m.MountPoint, drive, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The fs the supervisor most recently passed to <c>mount/mount</c> for
    /// <paramref name="drive"/>.</summary>
    public string LastFsFor(string drive) =>
        MountRequests.Last(r => string.Equals(r.MountPoint, drive, StringComparison.OrdinalIgnoreCase)).Fs;

    public void SetStats(string drive, RcloneVfsStats stats)
    {
        lock (gate)
        {
            statsByDrive[drive] = stats;
        }
    }

    public RcloneVfsStats StatsFor(string drive)
    {
        lock (gate)
        {
            return statsByDrive.TryGetValue(drive, out var s) ? s : DefaultStats;
        }
    }

    public void SetQueue(string drive, IReadOnlyList<RcloneVfsQueueItem> queue)
    {
        lock (gate)
        {
            queueByDrive[drive] = queue;
        }
    }

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RcloneVersionInfo { Version = "v1.75.0-bs-obf-double" });

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            MountRequests.Add(request);
            mounts.RemoveAll(m => string.Equals(m.MountPoint, request.MountPoint, StringComparison.OrdinalIgnoreCase));
            mounts.Add(new RcloneMountInfo { Fs = request.Fs, MountPoint = request.MountPoint, MountedOn = time.GetUtcNow() });
        }

        log.Record($"mount|{request.MountPoint}");
        return Task.FromResult(new RcloneMountResult { MountPoint = request.MountPoint });
    }

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            UnmountRequests.Add(mountPoint);
            mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        }

        log.Record($"unmount|{mountPoint}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<RcloneMountInfo>>(mounts.ToList());
        }
    }

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RcloneListItem>>([]);

    public async Task<IReadOnlyList<string>> ListVfsAsync(CancellationToken cancellationToken)
    {
        RecordVfs("vfs/list", string.Empty);
        await MaybeHangAsync("vfs/list", cancellationToken).ConfigureAwait(false);

        if (FailVfsList)
        {
            throw new RcloneRcException("vfs/list", 500, "scripted vfs/list failure");
        }

        List<string> mounted;
        lock (gate)
        {
            mounted = mounts.Select(m => m.Fs).Distinct(StringComparer.Ordinal).ToList();
        }

        return VfsListOverride is { } over ? over(mounted) : mounted;
    }

    public async Task<RcloneVfsStats> GetVfsStatsAsync(string fs, CancellationToken cancellationToken)
    {
        RecordVfs("vfs/stats", fs);
        await MaybeHangAsync("vfs/stats", cancellationToken).ConfigureAwait(false);

        if (FailVfsStats)
        {
            throw new RcloneRcException("vfs/stats", 500, "scripted vfs/stats failure");
        }

        lock (gate)
        {
            if (FailNextVfsStats > 0)
            {
                FailNextVfsStats--;
                throw new RcloneRcException("vfs/stats", 500, "scripted one-off vfs/stats failure");
            }
        }

        var drive = DriveOf(fs);
        return drive is null ? DefaultStats : StatsFor(drive);
    }

    public async Task<IReadOnlyList<RcloneVfsQueueItem>> GetVfsQueueAsync(string fs, CancellationToken cancellationToken)
    {
        RecordVfs("vfs/queue", fs);
        await MaybeHangAsync("vfs/queue", cancellationToken).ConfigureAwait(false);

        if (FailVfsQueue)
        {
            throw new RcloneRcException("vfs/queue", 500, "scripted vfs/queue failure");
        }

        var drive = DriveOf(fs);
        lock (gate)
        {
            return drive is not null && queueByDrive.TryGetValue(drive, out var q) ? q : [];
        }
    }

    /// <summary>
    /// The drive whose VFS a <c>vfs/stats</c> / <c>vfs/queue</c> call names. Deliberately lenient,
    /// so that a supervisor which GUESSES at a VFS name (an indexed <c>fs[0]</c>, a trailing slash)
    /// gets a healthy, idle answer and goes on to unmount -- which the fail-closed tests then catch.
    /// A name nothing resolves to also gets a healthy idle answer, for the same reason: rule 7 says
    /// an unmatched name is "not guessed at", so asking rcd about it anyway must not be rewarded
    /// with an error that happens to keep the drive up.
    /// </summary>
    private string? DriveOf(string fs)
    {
        lock (gate)
        {
            return (mounts.FirstOrDefault(m => string.Equals(m.Fs, fs, StringComparison.Ordinal))
                    ?? mounts.FirstOrDefault(m => fs.StartsWith(m.Fs, StringComparison.OrdinalIgnoreCase)))?.MountPoint;
        }
    }

    private void RecordVfs(string endpoint, string fs)
    {
        lock (gate)
        {
            vfsCalls.Add($"{endpoint}|{fs}");
        }

        log.Record($"{endpoint}|{fs}");
    }

    private async Task MaybeHangAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!HangVfs)
        {
            return;
        }

        var call = new HungCall(endpoint, time.GetUtcNow());
        lock (gate)
        {
            hungCalls.Add(call);
        }

        HungEntries.Release();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            call.ReleasedAt = time.GetUtcNow();
        }
    }

    /// <summary>One <c>vfs/*</c> call that hung. <see cref="ReleasedAt"/> is set when its token was
    /// cancelled; it stays <see langword="null"/> if the caller abandoned the call instead.</summary>
    internal sealed class HungCall(string endpoint, DateTimeOffset enteredAt)
    {
        public string Endpoint { get; } = endpoint;

        public DateTimeOffset EnteredAt { get; } = enteredAt;

        public DateTimeOffset? ReleasedAt { get; set; }
    }
}

/// <summary>Builders for <c>vfs/stats</c> answers, independent of the implementer's helpers.</summary>
internal static class Vfs
{
    /// <summary>A <c>vfs/stats</c> answer with both cache sections present. Defaults: a quiet VFS
    /// that has listed the root once.</summary>
    public static RcloneVfsStats Stats(
        long bytesUsed = 0,
        long files = 0,
        long uploadsInProgress = 0,
        long uploadsQueued = 0,
        long erroredFiles = 0,
        long dirs = 1,
        long metadataFiles = 0) => new()
    {
        DiskCache = new RcloneVfsDiskCache
        {
            BytesUsed = bytesUsed,
            Files = files,
            UploadsInProgress = uploadsInProgress,
            UploadsQueued = uploadsQueued,
            ErroredFiles = erroredFiles,
        },
        MetadataCache = new RcloneVfsMetadataCache { Dirs = dirs, Files = metadataFiles },
    };

    /// <summary>The same stats with ONE of the six activity counters of rule 7 moved by
    /// <paramref name="delta"/>.</summary>
    public static RcloneVfsStats Bump(RcloneVfsStats s, string counter, long delta = 1) => counter switch
    {
        "diskCache.bytesUsed" => s with { DiskCache = s.DiskCache! with { BytesUsed = s.DiskCache.BytesUsed + delta } },
        "diskCache.files" => s with { DiskCache = s.DiskCache! with { Files = s.DiskCache.Files + delta } },
        "diskCache.uploadsInProgress" => s with { DiskCache = s.DiskCache! with { UploadsInProgress = s.DiskCache.UploadsInProgress + delta } },
        "diskCache.uploadsQueued" => s with { DiskCache = s.DiskCache! with { UploadsQueued = s.DiskCache.UploadsQueued + delta } },
        "metadataCache.dirs" => s with { MetadataCache = s.MetadataCache! with { Dirs = s.MetadataCache.Dirs + delta } },
        "metadataCache.files" => s with { MetadataCache = s.MetadataCache! with { Files = s.MetadataCache.Files + delta } },
        _ => throw new ArgumentOutOfRangeException(nameof(counter), counter, "not one of rule 7's six activity counters"),
    };
}
