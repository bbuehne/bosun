using System.Collections.Concurrent;
using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Support;

/// <summary>How a held call reacts to its token being cancelled (ADR-023 tests).</summary>
public enum HoldMode
{
    /// <summary>Returns the way <c>HostProbe</c> does: a <c>Cancelled</c> OUTCOME, no exception. The
    /// shape that makes a naive "link the token" fix count every suspend as a failed probe.</summary>
    ReturnCancelled,

    /// <summary>Throws <see cref="OperationCanceledException"/> when its token is cancelled.</summary>
    ThrowOnCancel,

    /// <summary>Ignores its token and finishes only when the test releases it, with a normal result
    /// (<c>Success</c>). Models a call that completes just as the suspend arrives: its result must
    /// still be discarded.</summary>
    IgnoreToken,
}

/// <summary>One call a test has held open: the test can wait until it has been entered, read the token
/// it was given, and release it.</summary>
internal sealed class HeldCall(HoldMode mode)
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HoldMode Mode { get; } = mode;

    /// <summary>When set, a callback that THROWS is registered on the call's token, to prove the
    /// suspend prologue survives a faulting cancellation callback.</summary>
    public bool RegisterThrowingCancellationCallback { get; init; }

    /// <summary>What a deep probe that is released without having been cancelled reports.</summary>
    public DeepProbeOutcome ReleaseDeepOutcome { get; init; } = DeepProbeOutcome.Success;

    public CancellationToken Token { get; private set; }

    /// <summary>The managed thread a cancellation callback on <see cref="Token"/> ran on, or 0 if none
    /// has. Lets a test prove the callbacks of a suspend's cancel run on a pool thread and not on the
    /// thread that called <c>SuspendAsync</c>.</summary>
    public int CancellationCallbackThreadId => Volatile.Read(ref callbackThreadId);

    private int callbackThreadId;

    public Task Entered => entered.Task;

    public void Release() => released.TrySetResult();

    /// <summary>Waits as the held call does. Returns true if it ended because its token was cancelled.</summary>
    public async Task<bool> WaitAsync(CancellationToken token)
    {
        Token = token;
        entered.TrySetResult();

        using var recording = token.Register(() => Volatile.Write(ref callbackThreadId, Environment.CurrentManagedThreadId));
        using var throwing = RegisterThrowingCancellationCallback
            ? token.Register(static () => throw new InvalidOperationException("a cancellation callback that throws"))
            : default;

        if (Mode == HoldMode.IgnoreToken)
        {
            await released.Task;
            return false;
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult());
        await Task.WhenAny(released.Task, cancelled.Task);

        if (token.IsCancellationRequested)
        {
            if (Mode == HoldMode.ThrowOnCancel)
            {
                token.ThrowIfCancellationRequested();
            }

            return true;
        }

        return false;
    }
}

/// <summary>
/// An <see cref="IProbe"/> over a <see cref="FakeProbe"/> that can hold the next shallow or deep
/// probe open, record the token it was handed, and finish it in each way a real probe can
/// (<see cref="HoldMode"/>). Never touches a network.
/// </summary>
internal sealed class PreemptableProbe(FakeProbe inner) : IProbe
{
    private readonly object gate = new();
    private (string Hostname, HeldCall Call)? shallowHold;
    private (string HostKey, HeldCall Call)? deepHold;

    public HeldCall HoldNextShallow(string hostname, HoldMode mode, bool throwingCallback = false)
    {
        var call = new HeldCall(mode) { RegisterThrowingCancellationCallback = throwingCallback };
        lock (gate)
        {
            shallowHold = (hostname, call);
        }

        return call;
    }

    public HeldCall HoldNextDeep(
        string hostKey, HoldMode mode, DeepProbeOutcome releaseWith = DeepProbeOutcome.Success)
    {
        var call = new HeldCall(mode) { ReleaseDeepOutcome = releaseWith };
        lock (gate)
        {
            deepHold = (hostKey, call);
        }

        return call;
    }

    public async Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        HeldCall? held = null;
        lock (gate)
        {
            if (shallowHold is { } h && string.Equals(h.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
            {
                held = h.Call;
                shallowHold = null;
            }
        }

        if (held is null)
        {
            return await inner.ProbeShallowAsync(hostname, port, timeout, cancellationToken);
        }

        var cancelled = await held.WaitAsync(cancellationToken);
        return new ShallowProbeResult
        {
            Outcome = cancelled ? ShallowProbeOutcome.Cancelled : ShallowProbeOutcome.Success,
            Elapsed = TimeSpan.Zero,
            Detail = cancelled ? "held probe cancelled" : null,
        };
    }

    public async Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        HeldCall? held = null;
        lock (gate)
        {
            if (deepHold is { } h && string.Equals(h.HostKey, hostKey, StringComparison.Ordinal))
            {
                held = h.Call;
                deepHold = null;
            }
        }

        if (held is null)
        {
            return await inner.ProbeDeepAsync(hostKey, timeout, cancellationToken);
        }

        var cancelled = await held.WaitAsync(cancellationToken);
        return new DeepProbeResult
        {
            Outcome = cancelled ? DeepProbeOutcome.Cancelled : held.ReleaseDeepOutcome,
            Elapsed = TimeSpan.Zero,
            Detail = cancelled ? "held probe cancelled"
                : held.ReleaseDeepOutcome == DeepProbeOutcome.Success ? null : $"held probe released as {held.ReleaseDeepOutcome}",
        };
    }
}

/// <summary>
/// An <see cref="IRcloneClient"/> over <see cref="FakeRcloneMountClient"/> that can hold the next
/// <c>mount/mount</c>, <c>mount/unmount</c> or <c>vfs/list</c> open and records the token each was
/// given. Used to prove what a suspend does NOT cancel, and to hold a call that completes anyway.
/// </summary>
internal sealed class HoldingRcloneClient(FakeRcloneMountClient inner) : IRcloneClient
{
    private HeldCall? mountHold;
    private HeldCall? unmountHold;
    private HeldCall? vfsListHold;
    private HeldCall? getVersionHold;
    private HeldCall? listMountsHold;

    public FakeRcloneMountClient Inner => inner;

    /// <summary>The token of every <c>mount/unmount</c> call, in order.</summary>
    public ConcurrentQueue<CancellationToken> UnmountTokens { get; } = new();

    public HeldCall HoldNextMount(HoldMode mode = HoldMode.IgnoreToken) => mountHold = new HeldCall(mode);

    public HeldCall HoldNextUnmount(HoldMode mode = HoldMode.IgnoreToken) => unmountHold = new HeldCall(mode);

    public HeldCall HoldNextVfsList(HoldMode mode) => vfsListHold = new HeldCall(mode);

    /// <summary>Holds the next <c>core/version</c> (bs-5ga.1: the first call of a re-derivation).</summary>
    public HeldCall HoldNextGetVersion(HoldMode mode = HoldMode.IgnoreToken) => getVersionHold = new HeldCall(mode);

    /// <summary>Holds the next <c>mount/listmounts</c> (bs-5ga.1: the second call of a re-derivation).</summary>
    public HeldCall HoldNextListMounts(HoldMode mode = HoldMode.IgnoreToken) => listMountsHold = new HeldCall(mode);

    public async Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref getVersionHold, null) is { } held)
        {
            await held.WaitAsync(cancellationToken);
        }

        return await inner.GetVersionAsync(cancellationToken);
    }

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        inner.CreateConfigAsync(remoteName, type, parameters, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        inner.GetConfigAsync(remoteName, cancellationToken);

    public async Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref mountHold, null) is { } held)
        {
            await held.WaitAsync(cancellationToken);
        }

        return await inner.MountAsync(request, cancellationToken);
    }

    public async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        UnmountTokens.Enqueue(cancellationToken);
        if (Interlocked.Exchange(ref unmountHold, null) is { } held)
        {
            await held.WaitAsync(cancellationToken);
        }

        await inner.UnmountAsync(mountPoint, cancellationToken);
    }

    public async Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref listMountsHold, null) is { } held)
        {
            await held.WaitAsync(cancellationToken);
        }

        return await inner.ListMountsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        inner.ListAsync(fs, remote, cancellationToken);

    public async Task<IReadOnlyList<string>> ListVfsAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref vfsListHold, null) is { } held)
        {
            var cancelled = await held.WaitAsync(cancellationToken);
            if (cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The inner fake counts the call and answers normally. The token is not passed on for a
            // held call, so a call released with HoldMode.IgnoreToken completes even though its
            // token was cancelled.
            return await inner.ListVfsAsync(CancellationToken.None);
        }

        return await inner.ListVfsAsync(cancellationToken);
    }

    public Task<RcloneVfsStats> GetVfsStatsAsync(string fs, CancellationToken cancellationToken) =>
        inner.GetVfsStatsAsync(fs, cancellationToken);

    public Task<IReadOnlyList<RcloneVfsQueueItem>> GetVfsQueueAsync(string fs, CancellationToken cancellationToken) =>
        inner.GetVfsQueueAsync(fs, cancellationToken);
}

/// <summary>Records every log call and lets a test wait for one, so an assertion about a line that is
/// written from a thread-pool callback does not have to guess when it will arrive.</summary>
internal sealed class SignallingLogger : ILogger<MountSupervisor>
{
    private readonly object gate = new();
    private readonly List<CapturedLog> entries = [];
    private readonly List<(Func<CapturedLog, bool> Match, TaskCompletionSource Signal)> waiters = [];

    internal sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToList();
            }
        }
    }

    public Task WhenLoggedAsync(Func<CapturedLog, bool> match)
    {
        lock (gate)
        {
            if (entries.Any(match))
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((match, signal));
            return signal.Task;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var entry = new CapturedLog(logLevel, formatter(state, exception), exception);
        lock (gate)
        {
            entries.Add(entry);
            foreach (var waiter in waiters.Where(w => w.Match(entry)).ToList())
            {
                waiter.Signal.TrySetResult();
                waiters.Remove(waiter);
            }
        }
    }
}

/// <summary>
/// Drives a real <see cref="MountSupervisor"/> for the ADR-023 preemption tests, on fakes and a fake
/// clock only. Like <see cref="SupervisorHarness"/> it pumps the channel with <c>DrainAsync</c>, but
/// it also exposes what those tests need and the shared harness cannot: probes and rclone calls that
/// can be held open, the tokens they were given, and timers that fire WITHOUT the channel being
/// pumped (<see cref="FireTimers"/>), so work can be queued ahead of a suspend.
/// </summary>
/// <remarks>
/// Every wait is on a task the supervisor or a fake completes; <see cref="Guard"/> is only there so a
/// regression fails fast instead of hanging the run. No test using this reaches a real mount, rclone,
/// host, drive letter or wt.exe.
/// </remarks>
internal sealed class PreemptionHarness
{
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public PreemptionHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new FakeProbe();
        Gate = new PreemptableProbe(Probe);
        RcloneFake = new FakeRcloneMountClient();
        Rclone = new HoldingRcloneClient(RcloneFake);
        Supervisor = new MountSupervisor(new FakeHostConfigStore(config), Rclone, Gate, DriveTypes, Time, Log);
    }

    public FakeTimeProvider Time { get; }

    public FakeProbe Probe { get; }

    public PreemptableProbe Gate { get; }

    public FakeRcloneMountClient RcloneFake { get; }

    public HoldingRcloneClient Rclone { get; }

    public FakeDriveTypeReader DriveTypes { get; } = new();

    public SignallingLogger Log { get; } = new();

    public MountSupervisor Supervisor { get; }

    public static string Hostname(string key) => $"{key}.example.internal";

    /// <summary>Queues <paramref name="operation"/>, pumps the channel until it and everything it
    /// chains into has run, then awaits its own task so an exception surfaces.</summary>
    public async Task RunAsync(Func<Task> operation)
    {
        var task = operation();
        await Supervisor.DrainAsync().WaitAsync(Guard);
        await task.WaitAsync(Guard);
    }

    public Task StartAsync() => RunAsync(() => Supervisor.StartAsync());

    public Task SuspendAsync() => RunAsync(() => Supervisor.SuspendAsync());

    public Task ResumeAsync() => RunAsync(() => Supervisor.ResumeAsync());

    /// <summary>Moves the fake clock, which fires due timers (they enqueue), WITHOUT pumping. What the
    /// timers queued runs at the next <see cref="Pump"/> or <c>DrainAsync</c>.</summary>
    public void FireTimers(TimeSpan delta) => Time.Advance(delta);

    public Task Pump() => Supervisor.DrainAsync().WaitAsync(Guard);

    /// <summary>Advances the clock and pumps, like <see cref="SupervisorHarness.AdvanceAsync"/>.</summary>
    public async Task AdvanceAsync(TimeSpan delta)
    {
        Time.Advance(delta);
        await Pump();
    }

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    /// <summary>One host's transitions, oldest first.</summary>
    public IReadOnlyList<MountTransitionEntry> Transitions(string hostKey) =>
        Supervisor.GetTransitionHistory().Reverse().Where(t => t.HostKey == hostKey).ToList();

    /// <summary>The cause of every <c>X -&gt; Draining</c> transition for the host, oldest first.</summary>
    public IReadOnlyList<DrainCause> DrainCauses(string hostKey) =>
        Transitions(hostKey).Where(t => t.To == MountState.Draining && t.DrainCause is not null).Select(t => t.DrainCause!.Value).ToList();

    /// <summary>Calls <c>SuspendAsync</c> while <paramref name="pump"/> (a channel pump already running a
    /// held tick or command) is in flight, then runs everything to completion. The pump is awaited first
    /// and the channel pumped once more, because a pump that finds the channel empty before the suspend
    /// has been queued returns, and the suspend then has to be run by a second pump -- never by two at once.</summary>
    public async Task SuspendDuringAsync(Task pump)
    {
        var suspend = Supervisor.SuspendAsync();
        await pump.WaitAsync(Guard);
        await Pump();
        await suspend.WaitAsync(Guard);
    }

    public int InformationLines(string contains) =>
        Log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains(contains, StringComparison.Ordinal));
}
