using Bosun.Rclone;

namespace Bosun.Tests.Supervisor.Independent.RepairAll;

/// <summary>
/// An <see cref="IRcloneClient"/> double for the bs-aoz repair tests. It keeps a real in-memory
/// mount table, so <c>mount/listmounts</c> reports reality rather than the supervisor's intent.
/// </summary>
/// <remarks>
/// <para>
/// Two ways for an unmount to go wrong, because they catch different bugs:
/// </para>
/// <list type="bullet">
/// <item><see cref="IgnoreUnmounts"/>: the call returns normally but the drive stays. Only a
/// supervisor that re-asks <c>mount/listmounts</c> can tell. This is the case that catches a drain
/// that trusts the unmount call's own return.</item>
/// <item><see cref="FailUnmount"/>: the call throws (an <see cref="RcloneRcException"/> or an
/// <see cref="RcloneRcTimeoutException"/>) and the drive stays.</item>
/// </list>
/// <para>
/// Every fault budget is bounded, so a supervisor that retries without limit fails an assertion
/// rather than hanging the run. Nothing here touches a real rclone, WinFsp, or drive letter.
/// </para>
/// </remarks>
internal sealed class RepairRcloneDouble(RepairEventLog log) : IRcloneClient
{
    private readonly object gate = new();
    private readonly List<RcloneMountInfo> mounts = [];
    private readonly Dictionary<string, int> ignoredUnmountsLeft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Budget> faults = new(StringComparer.OrdinalIgnoreCase);

    public bool IsMounted(string drive)
    {
        lock (gate)
        {
            return mounts.Any(m => Same(m.MountPoint, drive));
        }
    }

    /// <summary>The next <paramref name="times"/> <c>mount/unmount</c> calls at
    /// <paramref name="drive"/> return normally and leave the drive mounted.</summary>
    public void IgnoreUnmounts(string drive, int times)
    {
        lock (gate)
        {
            ignoredUnmountsLeft[drive] = times;
        }
    }

    public void FailMount(string drive, Func<Exception> fault, int times) => SetFault(Key(RepairEventLog.Mount, drive), fault, times);

    public void FailUnmount(string drive, Func<Exception> fault, int times) => SetFault(Key(RepairEventLog.Unmount, drive), fault, times);

    public void FailListMounts(Func<Exception> fault, int times) => SetFault(RepairEventLog.ListMounts, fault, times);

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RcloneVersionInfo { Version = "v1.71.2-repair-double" });

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        if (TryTakeFault(Key(RepairEventLog.Mount, request.MountPoint), out var fault))
        {
            log.Record(RepairEventLog.Mount, request.MountPoint, ok: false);
            return Task.FromException<RcloneMountResult>(fault());
        }

        log.Record(RepairEventLog.Mount, request.MountPoint, ok: true);
        lock (gate)
        {
            mounts.RemoveAll(m => Same(m.MountPoint, request.MountPoint));
            mounts.Add(new RcloneMountInfo { Fs = request.Fs, MountPoint = request.MountPoint, MountedOn = DateTimeOffset.UnixEpoch });
        }

        log.Record(RepairEventLog.EffectMounted, request.MountPoint, ok: true);
        return Task.FromResult(new RcloneMountResult { MountPoint = request.MountPoint });
    }

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        if (TryTakeFault(Key(RepairEventLog.Unmount, mountPoint), out var fault))
        {
            log.Record(RepairEventLog.Unmount, mountPoint, ok: false);
            return Task.FromException(fault());
        }

        log.Record(RepairEventLog.Unmount, mountPoint, ok: true);

        bool removed;
        lock (gate)
        {
            if (ignoredUnmountsLeft.TryGetValue(mountPoint, out var left) && left > 0)
            {
                ignoredUnmountsLeft[mountPoint] = left - 1;
                removed = false;
            }
            else
            {
                removed = mounts.RemoveAll(m => Same(m.MountPoint, mountPoint)) > 0;
            }
        }

        if (removed)
        {
            log.Record(RepairEventLog.EffectUnmounted, mountPoint, ok: true);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        if (TryTakeFault(RepairEventLog.ListMounts, out var fault))
        {
            log.Record(RepairEventLog.ListMounts, null, ok: false);
            return Task.FromException<IReadOnlyList<RcloneMountInfo>>(fault());
        }

        List<RcloneMountInfo> snapshot;
        lock (gate)
        {
            snapshot = mounts.ToList();
        }

        log.Record(RepairEventLog.ListMounts, null, ok: true, snapshot.Select(m => m.MountPoint).ToList());
        return Task.FromResult<IReadOnlyList<RcloneMountInfo>>(snapshot);
    }

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RcloneListItem>>([]);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Key(string operation, string drive) => $"{operation}|{drive}";

    private void SetFault(string key, Func<Exception> fault, int times)
    {
        lock (gate)
        {
            faults[key] = new Budget(fault, times);
        }
    }

    private bool TryTakeFault(string key, out Func<Exception> fault)
    {
        lock (gate)
        {
            if (faults.TryGetValue(key, out var budget) && budget.Remaining > 0)
            {
                budget.Remaining--;
                fault = budget.Factory;
                return true;
            }
        }

        fault = null!;
        return false;
    }

    private sealed class Budget(Func<Exception> factory, int remaining)
    {
        public Func<Exception> Factory { get; } = factory;

        public int Remaining { get; set; } = remaining;
    }
}
