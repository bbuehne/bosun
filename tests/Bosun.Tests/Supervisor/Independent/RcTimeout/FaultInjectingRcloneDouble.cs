using Bosun.Rclone;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>One entry in <see cref="FaultInjectingRcloneDouble.Calls"/>.</summary>
/// <param name="Sequence">Global order across every entry.</param>
/// <param name="Operation">An rc endpoint (<c>mount/unmount</c> etc.), or an
/// <c>effect/...</c> entry that records a real change to the fake's mount table.</param>
/// <param name="MountPoint">The drive the call or effect concerns, if any.</param>
/// <param name="Faulted">True when the call threw.</param>
/// <param name="Listed">For a successful <c>mount/listmounts</c>, the mount points it returned.</param>
/// <param name="TransitionsBefore">How many state transitions the supervisor had recorded when
/// this entry was written. This places rc calls and transitions in one order, which is what
/// lets a test check "Disabled only after listmounts confirmed the drive gone".</param>
/// <param name="At">The fake clock's time.</param>
internal sealed record RcCall(
    long Sequence,
    string Operation,
    string? MountPoint,
    bool Faulted,
    IReadOnlyList<string>? Listed,
    int TransitionsBefore,
    DateTimeOffset At);

/// <summary>
/// An <see cref="IRcloneClient"/> double for bs-x57. It keeps a real in-memory mount table and
/// can make any of the four supervisor-facing calls fail a bounded number of times with any
/// exception.
/// </summary>
/// <remarks>
/// <para>
/// <b>Faults are bounded.</b> Each fault budget runs out after a set number of calls. A test that
/// needs "fails, then recovers" gets it, and a supervisor that retries without bound fails an
/// assertion instead of hanging the run.
/// </para>
/// <para>
/// <b>A fault normally has no effect.</b> A timed-out <c>mount/unmount</c> leaves the drive
/// mounted. That is the worst case for a drain, and it is the incident: the drive is still there
/// and the supervisor must not believe otherwise. <see cref="FailMount"/> can instead apply the
/// effect and then throw. That models rclone completing the mount after the HTTP call has
/// already timed out.
/// </para>
/// <para>
/// <b>Thread use.</b> In <c>RunAsync</c> tests the supervisor calls this from its loop thread
/// while the test thread reads it. Every member takes the same lock.
/// </para>
/// <para>
/// Nothing here touches a real <c>rclone rcd</c>, WinFsp, or a drive letter. The mount table is a
/// list.
/// </para>
/// </remarks>
internal sealed class FaultInjectingRcloneDouble(TimeProvider time) : IRcloneClient
{
    public const string Version = "core/version";
    public const string Mount = "mount/mount";
    public const string Unmount = "mount/unmount";
    public const string ListMounts = "mount/listmounts";
    public const string EffectMounted = "effect/mounted";
    public const string EffectUnmounted = "effect/unmounted";

    private readonly object gate = new();
    private readonly List<RcloneMountInfo> mounts = [];
    private readonly List<RcCall> calls = [];
    private readonly Dictionary<string, FaultBudget> faults = new(StringComparer.OrdinalIgnoreCase);
    private long sequence;

    /// <summary>Supplies the supervisor's current transition count. The harness wires this to
    /// <c>GetTransitionHistory().Count</c>.</summary>
    public Func<int> TransitionCount { get; set; } = () => 0;

    /// <summary>Called, outside the lock, after every entry is recorded. <c>RunAsync</c> tests use
    /// it to complete a <see cref="TaskCompletionSource"/> when a particular call happens.</summary>
    public Action<RcCall>? OnCall { get; set; }

    /// <summary>Awaited at the start of every <see cref="UnmountAsync"/> with the supervisor's
    /// token. Lets a test hold an unmount in flight until that token is cancelled (shutdown).</summary>
    public Func<string, CancellationToken, Task>? BeforeUnmount { get; set; }

    public IReadOnlyList<RcCall> Calls
    {
        get
        {
            lock (gate)
            {
                return calls.ToList();
            }
        }
    }

    public bool IsMounted(string mountPoint)
    {
        lock (gate)
        {
            return mounts.Any(m => Same(m.MountPoint, mountPoint));
        }
    }

    public int Count(string operation, string? mountPoint = null) =>
        Calls.Count(c => c.Operation == operation && (mountPoint is null || Same(c.MountPoint, mountPoint)));

    public int FaultedCount(string operation, string? mountPoint = null) =>
        Calls.Count(c => c.Faulted && c.Operation == operation && (mountPoint is null || Same(c.MountPoint, mountPoint)));

    /// <summary>The next <paramref name="times"/> <c>mount/mount</c> calls at
    /// <paramref name="mountPoint"/> throw. With <paramref name="mountedAnyway"/>, rclone still
    /// completes the mount; only the HTTP call fails.</summary>
    public void FailMount(string mountPoint, Func<Exception> fault, int times, bool mountedAnyway = false) =>
        SetFault(Key(Mount, mountPoint), new FaultBudget(fault, times, mountedAnyway));

    /// <summary>The next <paramref name="times"/> <c>mount/unmount</c> calls at
    /// <paramref name="mountPoint"/> throw, and the drive stays mounted.</summary>
    public void FailUnmount(string mountPoint, Func<Exception> fault, int times) =>
        SetFault(Key(Unmount, mountPoint), new FaultBudget(fault, times, applyEffect: false));

    /// <summary>The next <paramref name="times"/> <c>mount/listmounts</c> calls throw.</summary>
    public void FailListMounts(Func<Exception> fault, int times) =>
        SetFault(ListMounts, new FaultBudget(fault, times, applyEffect: false));

    /// <summary>The next <paramref name="times"/> <c>core/version</c> calls throw.</summary>
    public void FailVersion(Func<Exception> fault, int times) =>
        SetFault(Version, new FaultBudget(fault, times, applyEffect: false));

    /// <summary>Removes a mount without any rc call: rclone losing a mount out from under Bosun.</summary>
    public void DropMountSilently(string mountPoint)
    {
        RcCall entry;
        lock (gate)
        {
            mounts.RemoveAll(m => Same(m.MountPoint, mountPoint));
            entry = RecordLocked(EffectUnmounted, mountPoint, faulted: false, listed: null);
        }

        OnCall?.Invoke(entry);
    }

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (TryTakeFault(Version, out var fault))
        {
            Record(Version, null, faulted: true, listed: null);
            return Task.FromException<RcloneVersionInfo>(fault.Create());
        }

        Record(Version, null, faulted: false, listed: null);
        return Task.FromResult(new RcloneVersionInfo { Version = "v1.71.2-x57-double" });
    }

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        if (TryTakeFault(Key(Mount, request.MountPoint), out var fault))
        {
            if (fault.ApplyEffect)
            {
                AddMount(request);
            }

            Record(Mount, request.MountPoint, faulted: true, listed: null);
            return Task.FromException<RcloneMountResult>(fault.Create());
        }

        AddMount(request);
        Record(Mount, request.MountPoint, faulted: false, listed: null);
        return Task.FromResult(new RcloneMountResult { MountPoint = request.MountPoint });
    }

    public async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        if (BeforeUnmount is { } hook)
        {
            await hook(mountPoint, cancellationToken).ConfigureAwait(false);
        }

        if (TryTakeFault(Key(Unmount, mountPoint), out var fault))
        {
            Record(Unmount, mountPoint, faulted: true, listed: null);
            throw fault.Create();
        }

        Record(Unmount, mountPoint, faulted: false, listed: null);

        RcCall? effect = null;
        lock (gate)
        {
            if (mounts.RemoveAll(m => Same(m.MountPoint, mountPoint)) > 0)
            {
                effect = RecordLocked(EffectUnmounted, mountPoint, faulted: false, listed: null);
            }
        }

        if (effect is not null)
        {
            OnCall?.Invoke(effect);
        }
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        if (TryTakeFault(ListMounts, out var fault))
        {
            Record(ListMounts, null, faulted: true, listed: null);
            return Task.FromException<IReadOnlyList<RcloneMountInfo>>(fault.Create());
        }

        List<RcloneMountInfo> snapshot;
        RcCall entry;
        lock (gate)
        {
            snapshot = mounts.ToList();
            entry = RecordLocked(ListMounts, null, faulted: false, listed: snapshot.Select(m => m.MountPoint).ToList());
        }

        OnCall?.Invoke(entry);
        return Task.FromResult<IReadOnlyList<RcloneMountInfo>>(snapshot);
    }

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RcloneListItem>>([]);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Key(string operation, string mountPoint) => $"{operation}|{mountPoint}";

    private void SetFault(string key, FaultBudget budget)
    {
        lock (gate)
        {
            faults[key] = budget;
        }
    }

    private bool TryTakeFault(string key, out FaultBudget fault)
    {
        lock (gate)
        {
            if (faults.TryGetValue(key, out var budget) && budget.TryConsume())
            {
                fault = budget;
                return true;
            }
        }

        fault = null!;
        return false;
    }

    private void AddMount(RcloneMountRequest request)
    {
        RcCall entry;
        lock (gate)
        {
            mounts.RemoveAll(m => Same(m.MountPoint, request.MountPoint));
            mounts.Add(new RcloneMountInfo { Fs = request.Fs, MountPoint = request.MountPoint, MountedOn = DateTimeOffset.UnixEpoch });
            entry = RecordLocked(EffectMounted, request.MountPoint, faulted: false, listed: null);
        }

        OnCall?.Invoke(entry);
    }

    private void Record(string operation, string? mountPoint, bool faulted, IReadOnlyList<string>? listed)
    {
        RcCall entry;
        lock (gate)
        {
            entry = RecordLocked(operation, mountPoint, faulted, listed);
        }

        OnCall?.Invoke(entry);
    }

    private RcCall RecordLocked(string operation, string? mountPoint, bool faulted, IReadOnlyList<string>? listed)
    {
        var entry = new RcCall(++sequence, operation, mountPoint, faulted, listed, TransitionCount(), time.GetUtcNow());
        calls.Add(entry);
        return entry;
    }

    private sealed class FaultBudget(Func<Exception> factory, int times, bool applyEffect)
    {
        private int remaining = times;

        public bool ApplyEffect { get; } = applyEffect;

        public Exception Create() => factory();

        public bool TryConsume()
        {
            if (remaining <= 0)
            {
                return false;
            }

            remaining--;
            return true;
        }
    }
}
