using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>What a held call does when the token it was handed is cancelled.</summary>
public enum OnCancel
{
    /// <summary>What the production collaborator does. For a probe that is <c>HostProbe</c>: it
    /// returns the outcome <c>Cancelled</c> and does NOT throw (ADR-023's context, fourth bullet). For
    /// an rc call that is <c>RcloneClient</c>: a caller-token cancellation surfaces as
    /// <see cref="OperationCanceledException"/>.</summary>
    LikeProduction,

    /// <summary>Throw <see cref="OperationCanceledException"/> whatever the call is. A probe is allowed
    /// to do this by the <see cref="IProbe"/> contract even though <c>HostProbe</c> does not.</summary>
    Throw,

    /// <summary>Ignore the token and stay hung until the test releases the call. This is how a test
    /// makes a call return a REAL outcome (a failure, a Success) after its token was cancelled, which
    /// is ADR-023 decision 3's case: "decided by the token, never by the outcome".</summary>
    Ignore,
}

/// <summary>
/// Marks the thread that is inside a <see cref="MountSupervisor.SuspendAsync"/> call, so a held call
/// can tell whether its cancellation callback ran inline on that thread (ADR-023 decision 2: the
/// prologue uses <c>CancelAsync</c>, never <c>Cancel</c>, because in production that thread is
/// <c>SystemEvents</c>' single notification thread).
/// </summary>
internal static class SuspendCaller
{
    [ThreadStatic]
    private static bool inside;

    public static bool InsideSuspendCall => inside;

    /// <summary>Calls <c>SuspendAsync</c> on this thread with the marker set for exactly the duration
    /// of the synchronous part of the call.</summary>
    public static Task Call(IMountSupervisor supervisor)
    {
        inside = true;
        try
        {
            return supervisor.SuspendAsync();
        }
        finally
        {
            inside = false;
        }
    }
}

/// <summary>
/// One armed hold: the next matching call is captured, its token recorded, and it does not complete
/// until the test releases it or (unless <see cref="OnCancel.Ignore"/>) its token is cancelled.
/// </summary>
internal sealed class Hold
{
    private readonly TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource executing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource rcdWorkDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int cancelCallbackRanInsideSuspendCall;

    public Hold(OnCancel onCancel) => OnCancel = onCancel;

    public OnCancel OnCancel { get; }

    /// <summary>"shallow", "deep", "vfslist", "vfsstats", "vfsqueue", "listmounts", "version",
    /// "mount", "unmount".</summary>
    public string? Operation { get; private set; }

    public string? Target { get; private set; }

    public CancellationToken Token { get; private set; }

    public Task WhenArrived => arrived.Task;

    public bool HasArrived => arrived.Task.IsCompleted;

    /// <summary>
    /// Completes when the call has taken <c>rclone rcd</c>'s mount lock and its (simulated) work has
    /// begun (bs-6lu). For a call on <c>mount/mount</c>, <c>mount/unmount</c> or <c>mount/listmounts</c>
    /// that is later than <see cref="WhenArrived"/> whenever another such call is running: the request
    /// has left Bosun, but rcd is not executing it yet. Never completes for a call that does not take the
    /// lock (<c>core/version</c>, <c>vfs/*</c>, probes).
    /// </summary>
    public Task WhenExecuting => executing.Task;

    /// <summary>True once the call is running inside rcd (see <see cref="WhenExecuting"/>).</summary>
    public bool IsExecuting => executing.Task.IsCompleted;

    public bool IsPending => HasArrived && !outcome.Task.IsCompleted;

    /// <summary>
    /// True if the token handed to the held call has been cancelled -- by anyone. It becomes true
    /// asynchronously after a suspend (the held call's token is linked to the tick's, and
    /// <c>CancelAsync</c> runs the link's callback on the thread pool), and it stays false for good
    /// if the call is released and its linked source disposed first. Wait for it with
    /// <see cref="PreemptSpecHarness.WaitForAsync"/> before releasing an <see cref="OnCancel.Ignore"/>
    /// hold (bs-3jd). For a check that the call was NOT cancelled, use
    /// <see cref="CancellationRequested"/>: "still false" here proves nothing (bs-s30).
    /// </summary>
    public bool TokenCancelled => HasArrived && Token.IsCancellationRequested;

    /// <summary>
    /// True if cancellation has been REQUESTED for the held call: on its token, or on any source that
    /// token is linked to (<see cref="TokenLineage"/>). Unlike <see cref="TokenCancelled"/>, this does
    /// not wait for the thread pool. Once <c>SuspendAsync</c> has returned, a suspend that preempted
    /// this call has already set it. Use it for NEGATIVE checks ("the suspend did not cancel this
    /// call"), and read it while the call is still held: releasing the call lets the supervisor
    /// dispose the linked source. <see cref="TokenCancelled"/> can pass such a check vacuously
    /// (bs-s30).
    /// </summary>
    public bool CancellationRequested => HasArrived && TokenLineage.CancellationRequested(Token);

    /// <summary>True if a cancellation callback on the held call's token ran synchronously inside a
    /// <see cref="SuspendCaller.Call"/>.</summary>
    public bool CancelCallbackRanInsideSuspendCall => Volatile.Read(ref cancelCallbackRanInsideSuspendCall) == 1;

    /// <summary>Completes the held call. For a probe, <paramref name="value"/> is the outcome to
    /// report; for an rc call it is ignored and the call proceeds to the in-memory double.
    /// For a call that takes rcd's mount lock (bs-6lu), rcd's work for it is finished; the call still
    /// returns only once it has actually got the lock, so releasing a call that is queued behind
    /// another does not run it early.</summary>
    public void Release(object? value = null)
    {
        outcome.TrySetResult(value);
        rcdWorkDone.TrySetResult();
    }

    /// <summary>Completes the held call by throwing <paramref name="failure"/> from it, as
    /// <c>RcloneClient</c> does when rcd ANSWERS with an error. The call never reaches the in-memory
    /// double, so the mount table is left as it was (bs-5o4), and rcd's mount lock (bs-6lu) is free again
    /// at once: an error response means rcd's handler has returned. To model a call Bosun gave up on
    /// while rcd keeps running it (the 30 s call timeout), use <see cref="Abandon"/>.</summary>
    public void Fail(Exception failure)
    {
        outcome.TrySetException(failure);
        rcdWorkDone.TrySetResult();
    }

    /// <summary>
    /// The CLIENT gives up (<c>RcloneClient</c>'s 30 s call timeout, <c>RcloneRcTimeoutException</c>)
    /// but rcd does not: the call throws <paramref name="failure"/> to the caller now, and rcd's mount
    /// lock (bs-6lu) stays held until <see cref="FinishInRcd"/>. The unmount and listmounts handlers
    /// ignore their context, so a call Bosun abandons keeps running inside rcd and keeps the lock. Unlike
    /// <see cref="Fail"/>, rcd's work still happens: when <see cref="FinishInRcd"/> ends it, the call takes
    /// effect on the in-memory mount table (the abandoned unmount does unmount the drive, late), with nobody
    /// listening for the answer. A call abandoned while still queued behind another runs, and takes effect,
    /// when its turn comes and the test finishes it.
    /// </summary>
    public void Abandon(Exception failure)
    {
        Volatile.Write(ref abandoned, 1);
        outcome.TrySetException(failure);
    }

    /// <summary>rcd's work for this call ends: the mount lock is released (the next queued call runs).
    /// For an <see cref="Abandon"/>ed call that is the only way it ends. For a call that is not abandoned
    /// it is the same as <see cref="Release"/> as far as rcd is concerned.</summary>
    public void FinishInRcd() => rcdWorkDone.TrySetResult();

    private int abandoned;

    /// <summary>True if the call was <see cref="Abandon"/>ed (rcd's work survives the client giving up).</summary>
    internal bool Abandoned => Volatile.Read(ref abandoned) == 1;

    /// <summary>True once the caller has been given a failure for this call (<see cref="Fail"/>,
    /// <see cref="Abandon"/>, or a cancellation that threw).</summary>
    internal bool ClientFailed => outcome.Task.IsFaulted || outcome.Task.IsCanceled;

    internal Task<object?> ClientView => outcome.Task;

    internal void MarkExecuting() => executing.TrySetResult();

    internal Task RcdWorkDone => rcdWorkDone.Task;

    internal Task<object?> Begin(string operation, string target, CancellationToken token, Func<object?> cancelledValue)
    {
        Operation = operation;
        Target = target;
        Token = token;

        // Registered for every mode, so CancelCallbackRanInsideSuspendCall is observable even for a
        // call that ignores its token.
        token.Register(() =>
        {
            if (SuspendCaller.InsideSuspendCall)
            {
                Volatile.Write(ref cancelCallbackRanInsideSuspendCall, 1);
            }

            switch (OnCancel)
            {
                case OnCancel.LikeProduction when cancelledValue() is { } value:
                    outcome.TrySetResult(value);
                    break;
                case OnCancel.LikeProduction:
                case OnCancel.Throw:
                    // Ends the call, and with it rcd's mount lock (bs-6lu). This does NOT model rcd
                    // carrying on with an unmount or listmounts whose caller cancelled; a test that needs
                    // that uses OnCancel.Ignore plus Abandon.
                    outcome.TrySetException(new OperationCanceledException(token));
                    rcdWorkDone.TrySetResult();
                    break;
            }
        });

        arrived.TrySetResult();
        return outcome.Task;
    }
}

/// <summary>
/// An <see cref="IProbe"/> that forwards to <see cref="ProbeDouble"/> (so per-host scripting and
/// counts keep working) except for calls a test has armed a <see cref="Hold"/> for. Every call --
/// held or not -- is also recorded with the token it was handed.
/// </summary>
internal sealed class HoldableProbe(ProbeDouble inner) : IProbe
{
    private readonly object gate = new();
    private readonly List<(string Kind, string Target, Hold Hold)> armed = [];
    private readonly List<(string Kind, string Target, CancellationToken Token)> calls = [];

    public ProbeDouble Inner => inner;

    /// <summary>Arms a hold on the next shallow probe of <paramref name="hostname"/>.</summary>
    public Hold HoldNextShallow(string hostname, OnCancel onCancel = OnCancel.LikeProduction) => Arm("shallow", hostname, onCancel);

    /// <summary>Arms a hold on the next deep probe of <paramref name="hostKey"/>.</summary>
    public Hold HoldNextDeep(string hostKey, OnCancel onCancel = OnCancel.LikeProduction) => Arm("deep", hostKey, onCancel);

    /// <summary>Every shallow probe of <paramref name="hostname"/>, held or not.</summary>
    public int ShallowCalls(string hostname)
    {
        lock (gate)
        {
            return calls.Count(c => c.Kind == "shallow" && string.Equals(c.Target, hostname, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Every deep probe of <paramref name="hostKey"/>, held or not.</summary>
    public int DeepCalls(string hostKey)
    {
        lock (gate)
        {
            return calls.Count(c => c.Kind == "deep" && c.Target == hostKey);
        }
    }

    public async Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var hold = Take("shallow", hostname, cancellationToken);
        if (hold is null)
        {
            return await inner.ProbeShallowAsync(hostname, port, timeout, cancellationToken);
        }

        var value = await hold.Begin(
            "shallow", hostname, cancellationToken, () => (object)ShallowProbeOutcome.Cancelled);
        var outcome = (ShallowProbeOutcome)(value ?? ShallowProbeOutcome.Success);
        return new ShallowProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == ShallowProbeOutcome.Success ? null : $"held {outcome}",
        };
    }

    public async Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var hold = Take("deep", hostKey, cancellationToken);
        if (hold is null)
        {
            return await inner.ProbeDeepAsync(hostKey, timeout, cancellationToken);
        }

        var value = await hold.Begin("deep", hostKey, cancellationToken, () => (object)DeepProbeOutcome.Cancelled);
        var outcome = (DeepProbeOutcome)(value ?? DeepProbeOutcome.Success);
        return new DeepProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == DeepProbeOutcome.Success ? null : $"held {outcome}",
        };
    }

    private Hold Arm(string kind, string target, OnCancel onCancel)
    {
        var hold = new Hold(onCancel);
        lock (gate)
        {
            armed.Add((kind, target, hold));
        }

        return hold;
    }

    private Hold? Take(string kind, string target, CancellationToken token)
    {
        lock (gate)
        {
            calls.Add((kind, target, token));
            var index = armed.FindIndex(a => a.Kind == kind && string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            var hold = armed[index].Hold;
            armed.RemoveAt(index);
            return hold;
        }
    }
}

/// <summary>One recorded rc call: what, at which mount point or fs, when on the fake clock.</summary>
internal sealed record RcCall(DateTimeOffset At, string Operation, string? Target);

/// <summary>
/// An <see cref="IRcloneClient"/> that forwards to <see cref="RcloneDouble"/> (whose in-memory mount
/// table stays the one source of truth for what is mounted) except for calls a test has armed a
/// <see cref="Hold"/> for. A held rc call proceeds to the double only once released; if its token is
/// cancelled first it throws <see cref="OperationCanceledException"/>, as <c>RcloneClient</c> does.
/// Every call is timestamped on the fake clock. Never reaches a real rclone.
/// </summary>
/// <remarks>
/// <para>
/// <b>rcd's mount lock (bs-6lu).</b> <c>rclone rcd</c> holds one package-level mutex (<c>mountMu</c>,
/// <c>cmd/mountlib/rc.go</c>, verified at v1.75.0) for the whole of <c>mount/mount</c>,
/// <c>mount/unmount</c> and <c>mount/listmounts</c> (the real lock also covers <c>mount/types</c> and
/// <c>mount/unmountall</c>, which <see cref="IRcloneClient"/> does not expose). This fake models it: those
/// three calls run inside rcd one at a time, whatever order they arrive in. <c>core/version</c>,
/// <c>operations/list</c> and <c>vfs/*</c> do not take it and run in parallel with anything.
/// </para>
/// <para>
/// The request still <i>leaves Bosun at arrival</i>: <see cref="Calls"/>, <see cref="Count"/> and
/// <see cref="Times"/> record it the moment it is made, and <see cref="Hold.WhenArrived"/> fires then.
/// What waits is rcd's execution: a call that finds the lock taken queues (<see cref="Hold.IsExecuting"/>
/// stays false, the mount table is unchanged) and runs, in the order the semaphore grants, when the lock is
/// released. So with one unmount hung, the other hosts' requests are on the wire but their drives stay
/// mounted. Hold arrival order is therefore not execution order; a test must not depend on which of
/// several queued calls goes next.
/// </para>
/// <para>
/// <b>What leaves the lock.</b> A call releases it when rcd's work for it ends: it ran to completion
/// (unheld calls run at once; a held call when the test calls <see cref="Hold.Release"/>), or rcd answered
/// with an error (<see cref="Hold.Fail"/>). A call the CLIENT abandons keeps the lock if the test says so
/// with <see cref="Hold.Abandon"/> (the 30 s timeout: the unmount and listmounts handlers ignore their
/// context, so rcd keeps running them) until <see cref="Hold.FinishInRcd"/>. The one deliberate gap: a
/// held call whose token is cancelled under <see cref="OnCancel.LikeProduction"/> or
/// <see cref="OnCancel.Throw"/> ends and frees the lock, because in those modes the hang itself ends on
/// cancellation; a held call under <see cref="OnCancel.Ignore"/> keeps it until released, which is the
/// faithful case. A client-side failure never applies the call to the mount table (bs-5o4), as before.
/// </para>
/// <para>
/// Not modelled: how long rcd's work takes (a test chooses by when it calls Release or FinishInRcd), and
/// the real handler's own timing (up to a 10 s drive-letter poll inside <c>mount/unmount</c>).
/// </para>
/// </remarks>
internal sealed class HoldableRclone(RcloneDouble inner, TimeProvider time) : IRcloneClient
{
    private readonly object gate = new();
    private readonly List<(string[] Operations, string? Target, Hold Hold)> armed = [];
    private readonly List<RcCall> calls = [];

    public RcloneDouble Inner => inner;

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

    /// <summary>Arms a hold on the next call whose operation is any of <paramref name="operations"/>
    /// ("version", "listmounts", "mount", "unmount", "vfslist", "vfsstats", "vfsqueue").</summary>
    public Hold HoldNext(OnCancel onCancel, params string[] operations)
    {
        var hold = new Hold(onCancel);
        lock (gate)
        {
            armed.Add((operations, null, hold));
        }

        return hold;
    }

    /// <summary>Arms a hold on the next <paramref name="operation"/> call whose mount point or fs is
    /// <paramref name="target"/>. Needed since bs-5o4: the suspend sweep has several
    /// <c>mount/unmount</c> calls in flight at once, in an order the spec does not fix, so a test
    /// that wants one particular host's call held must say which.</summary>
    public Hold HoldNextAt(OnCancel onCancel, string operation, string target)
    {
        var hold = new Hold(onCancel);
        lock (gate)
        {
            armed.Add(([operation], target, hold));
        }

        return hold;
    }

    public int Count(string operation, string? target = null)
    {
        lock (gate)
        {
            return calls.Count(c => c.Operation == operation
                                    && (target is null || string.Equals(c.Target, target, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public IReadOnlyList<DateTimeOffset> Times(string operation, string target)
    {
        lock (gate)
        {
            return calls.Where(c => c.Operation == operation && string.Equals(c.Target, target, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.At)
                .ToList();
        }
    }

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        Through("version", null, cancellationToken, () => inner.GetVersionAsync(cancellationToken));

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        inner.CreateConfigAsync(remoteName, type, parameters, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        inner.GetConfigAsync(remoteName, cancellationToken);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken) =>
        ThroughRcdLock("mount", request.MountPoint, cancellationToken, () => inner.MountAsync(request, cancellationToken));

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken) =>
        ThroughRcdLock<object?>("unmount", mountPoint, cancellationToken, async () =>
        {
            await inner.UnmountAsync(mountPoint, cancellationToken);
            return null;
        });

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken) =>
        ThroughRcdLock("listmounts", null, cancellationToken, () => inner.ListMountsAsync(cancellationToken));

    /// <summary>True while a mount/* call is running inside rcd (holding its mount lock).</summary>
    public bool RcdMountLockHeld => rcdMountLock.CurrentCount == 0;

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        inner.ListAsync(fs, remote, cancellationToken);

    public Task<IReadOnlyList<string>> ListVfsAsync(CancellationToken cancellationToken) =>
        Through("vfslist", null, cancellationToken, () => inner.ListVfsAsync(cancellationToken));

    public Task<RcloneVfsStats> GetVfsStatsAsync(string fs, CancellationToken cancellationToken) =>
        Through("vfsstats", fs, cancellationToken, () => inner.GetVfsStatsAsync(fs, cancellationToken));

    public Task<IReadOnlyList<RcloneVfsQueueItem>> GetVfsQueueAsync(string fs, CancellationToken cancellationToken) =>
        Through("vfsqueue", fs, cancellationToken, () => inner.GetVfsQueueAsync(fs, cancellationToken));

    private Hold? RecordAndTake(string operation, string? target)
    {
        lock (gate)
        {
            calls.Add(new RcCall(time.GetUtcNow(), operation, target));
            var index = armed.FindIndex(a => a.Operations.Contains(operation)
                                             && (a.Target is null || string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase)));
            var hold = index < 0 ? null : armed[index].Hold;
            if (index >= 0)
            {
                armed.RemoveAt(index);
            }

            return hold;
        }
    }

    private readonly SemaphoreSlim rcdMountLock = new(1, 1);

    private readonly record struct RcdResult<T>(bool Applied, T? Value);

    /// <summary>
    /// A call that takes rcd's mount lock (see the class remarks). The request is recorded and its hold
    /// matched now, at arrival; execution waits for the lock and, for a held call, for the test to
    /// release it. rcd's side runs on its own and is NOT tied to the caller's token: an unheld call whose
    /// token is cancelled while it queues throws to its caller but still runs in rcd, as the real
    /// handlers (which ignore their context) would.
    /// </summary>
    private async Task<T> ThroughRcdLock<T>(string operation, string? target, CancellationToken token, Func<Task<T>> proceed)
    {
        var hold = RecordAndTake(operation, target);
        var clientView = hold?.Begin(operation, target ?? string.Empty, token, () => null);

        var server = RunInRcdAsync(hold, proceed);

        if (clientView is null)
        {
            return (await server.WaitAsync(token)).Value!;
        }

        var first = await Task.WhenAny(server, clientView);
        if (first == clientView && hold!.ClientFailed)
        {
            await clientView; // throws what the caller is to see
        }

        var result = await server;
        if (!result.Applied || hold!.ClientFailed)
        {
            await clientView; // the client failed first (rcd skipped the call, or finished it unheard): throws
        }

        return result.Value!;
    }

    private async Task<RcdResult<T>> RunInRcdAsync<T>(Hold? hold, Func<Task<T>> proceed)
    {
        await rcdMountLock.WaitAsync();
        try
        {
            if (hold is not null)
            {
                hold.MarkExecuting();
                await hold.RcdWorkDone;
                if (hold.ClientFailed && !hold.Abandoned)
                {
                    return new RcdResult<T>(false, default);
                }
            }

            Task<T> result;
            lock (doubleGate)
            {
                result = proceed();
            }

            return new RcdResult<T>(true, await result);
        }
        finally
        {
            rcdMountLock.Release();
        }
    }

    private async Task<T> Through<T>(string operation, string? target, CancellationToken token, Func<Task<T>> proceed)
    {
        var hold = RecordAndTake(operation, target);

        if (hold is not null)
        {
            await hold.Begin(operation, target ?? string.Empty, token, () => null);
        }

        // The in-memory double is not thread-safe, and released holds resume on pool threads: since
        // bs-5o4 the supervisor has several rc calls in flight at once, so two of them can be released
        // together. The double's calls complete synchronously, so the lock covers all of their work.
        Task<T> result;
        lock (doubleGate)
        {
            result = proceed();
        }

        return await result;
    }

    private readonly object doubleGate = new();
}

internal static class HoldExtensions
{
    /// <summary>
    /// Plays rcd finishing its work for <paramref name="holds"/> (mount/* calls) one at a time, in whatever
    /// order its lock grants them (bs-6lu): waits for whichever is running inside rcd, finishes it
    /// (<see cref="Hold.FinishInRcd"/>), and repeats. Only one hold can be executing at a time, so the
    /// order the lock picks never matters to the caller. Fails after <see cref="PreemptSpecHarness.Guard"/>
    /// if no remaining hold ever starts executing.
    /// </summary>
    public static async Task FinishInRcdInTurnAsync(this IEnumerable<Hold> holds)
    {
        var pending = holds.ToList();
        while (pending.Count > 0)
        {
            var running = await WhenAnyExecutingAsync(pending);
            running.FinishInRcd();
            pending.Remove(running);
        }
    }

    private static async Task<Hold> WhenAnyExecutingAsync(List<Hold> pending)
    {
        var any = await Task.WhenAny(pending.Select(h => h.WhenExecuting.ContinueWith(_ => h, TaskScheduler.Default)))
            .WaitAsync(PreemptSpecHarness.Guard);
        return await any;
    }
}
