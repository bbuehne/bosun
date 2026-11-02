using Bosun.Rclone;

namespace Bosun.Tests.Rclone.Fakes;

/// <summary>
/// What every <see cref="IRcloneClient"/> test double answers to the VFS calls (<c>vfs/list</c>,
/// <c>vfs/stats</c>, <c>vfs/queue</c>) unless a test scripts something else: one idle, healthy VFS
/// per mounted fs, with constant counters and an empty upload queue. It keeps every pre-existing
/// supervisor test that never cares about activity behaving as it did before the idle unmount
/// started reading rclone's VFS (bs-obf): nothing changes, so the idle timer runs out as it always
/// did.
/// </summary>
internal static class IdleVfs
{
    public static IReadOnlyList<string> NamesFor(IEnumerable<RcloneMountInfo> mounts) =>
        mounts.Select(m => m.Fs).Distinct(StringComparer.Ordinal).ToList();

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

    public static IReadOnlyList<RcloneVfsQueueItem> EmptyQueue { get; } = [];
}
