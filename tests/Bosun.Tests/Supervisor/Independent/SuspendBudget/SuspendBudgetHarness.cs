using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Independent.SuspendBudget;

/// <summary>
/// Drives a <see cref="MountSupervisor"/> for the bs-u4a suspend-budget tests. Same pump strategy as
/// <see cref="IndependentHarness"/> (enqueue, then drain the channel), plus the two things those
/// tests need that the shared harness does not record: <b>when</b> each <c>mount/unmount</c> call
/// happened on the fake clock, and <b>when</b> the supervisor escalated a drain to a forced unmount.
/// </summary>
/// <remarks>
/// <para>
/// <b>How escalation is observed.</b> docs/ARCHITECTURE.md §4 rule 4 calls it "escalate to a forced
/// unmount", but <see cref="IRcloneClient"/> has no distinct force endpoint, so at the rclone seam a
/// forced unmount is indistinguishable from an ordinary one. The one place the supervisor says which
/// it is doing is its warning-level log line. <see cref="EscalationTimes"/> therefore keys on a
/// <see cref="LogLevel.Warning"/> entry that names the host and contains "escalat". If that wording
/// changes, every escalation-time test fails loudly with "no escalation recorded" -- never silently
/// passes -- so the coupling costs a message edit, not a missed defect.
/// </para>
/// <para>
/// Nothing here reaches a real drive letter, <c>rclone rcd</c>, WinFsp, a real host, or the real
/// fragment path: the collaborators are in-memory doubles and a fake clock. Time moves only when a
/// test calls <see cref="AdvanceAsync"/>.
/// </para>
/// </remarks>
internal sealed class SuspendBudgetHarness
{
    public SuspendBudgetHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new ProbeDouble(Log);
        Gate = new GatedProbe(Probe);
        Rclone = new RcloneDouble(Log);
        TimedRclone = new TimedRcloneClient(Rclone, Time);
        Logger = new TimedLogger(Time);
        Store = new ReloadableConfigStoreDouble(config);
        Supervisor = new MountSupervisor(
            Store, TimedRclone, Gate, new Bosun.Tests.Supervisor.Fakes.FakeDriveTypeReader(), Time, Logger);
    }

    public FakeTimeProvider Time { get; }

    public SupervisorCallLog Log { get; } = new();

    public ProbeDouble Probe { get; }

    /// <summary>Wraps <see cref="Probe"/>; can hold one deep probe open so a host can be caught
    /// mid-<c>Mounting</c>.</summary>
    public GatedProbe Gate { get; }

    public RcloneDouble Rclone { get; }

    public TimedRcloneClient TimedRclone { get; }

    public TimedLogger Logger { get; }

    public ReloadableConfigStoreDouble Store { get; }

    public MountSupervisor Supervisor { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public async Task RunAsync(Func<Task> operation)
    {
        var task = operation();
        await Supervisor.DrainAsync();
        await task;
    }

    public Task StartAsync() => RunAsync(() => Supervisor.StartAsync());

    public Task SuspendAsync() => RunAsync(() => Supervisor.SuspendAsync());

    public Task ResumeAsync() => RunAsync(() => Supervisor.ResumeAsync());

    /// <summary>A successful config reload: the store's <c>Current</c> becomes
    /// <paramref name="next"/>, then the supervisor's reload command runs to completion.</summary>
    public Task ReloadAsync(BosunConfig next)
    {
        Store.Publish(next);
        return RunAsync(() => Supervisor.ConfigChangedAsync(next));
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="delta"/> in small steps, pumping the supervisor's
    /// channel after each one.
    /// </summary>
    /// <remarks>
    /// Stepping is what makes the times in these tests mean anything. A timer callback only
    /// <i>enqueues</i> work; the work runs when the channel is pumped. One big
    /// <c>Time.Advance</c> followed by one pump would run every due callback with the clock already
    /// at the END of the window, so a log line or an elapsed-time check inside the supervisor would
    /// see the wrong time, and a chained timer (each probe arms the next) would fire only once per
    /// advance. Production pumps continuously, so the step is the test's approximation of "promptly":
    /// 1 ms for windows up to a minute (where tests assert exact instants), 1 s beyond that (where
    /// they assert only states and counts).
    /// </remarks>
    public async Task AdvanceAsync(TimeSpan delta)
    {
        var step = delta <= TimeSpan.FromMinutes(1) ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(1);
        var target = Now + delta;
        while (Now < target)
        {
            var next = target - Now < step ? target - Now : step;
            Time.Advance(next);
            await Supervisor.DrainAsync();
        }

        await Supervisor.DrainAsync();
    }

    public Task AdvanceSecondsAsync(double seconds) => AdvanceAsync(TimeSpan.FromSeconds(seconds));

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    public bool IsSupervised(string hostKey) => Supervisor.GetSnapshot().Any(h => h.HostKey == hostKey);

    /// <summary><paramref name="hostKey"/>'s transitions, OLDEST FIRST. (GetTransitionHistory
    /// returns newest first.)</summary>
    public IReadOnlyList<MountTransitionEntry> Transitions(string hostKey) =>
        Supervisor.GetTransitionHistory().Reverse().Where(t => t.HostKey == hostKey).ToList();

    /// <summary>Fake-clock times at which the supervisor logged that it was escalating
    /// <paramref name="hostKey"/>'s drain to a forced unmount. See the class remarks.</summary>
    public IReadOnlyList<DateTimeOffset> EscalationTimes(string hostKey) =>
        Logger.Entries
            .Where(e => e.Level == LogLevel.Warning
                        && e.Message.Contains(hostKey, StringComparison.Ordinal)
                        && e.Message.Contains("escalat", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.At)
            .ToList();

    /// <summary>Fake-clock times of every <c>mount/unmount</c> call at <paramref name="mountPoint"/>.</summary>
    public IReadOnlyList<DateTimeOffset> UnmountTimes(string mountPoint) =>
        TimedRclone.Calls
            .Where(c => c.Operation == "unmount" && string.Equals(c.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.At)
            .ToList();

    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";
}

/// <summary>One recorded rclone call: what, where, and when on the fake clock.</summary>
internal sealed record TimedRcloneCall(DateTimeOffset At, string Operation, string? MountPoint);

/// <summary>
/// Delegates every call to an <see cref="RcloneDouble"/> (so the in-memory mount table stays the
/// single source of truth for what is mounted) and records each mount/unmount with the fake clock's
/// time. Never touches a real rclone.
/// </summary>
internal sealed class TimedRcloneClient(RcloneDouble inner, TimeProvider time) : IRcloneClient
{
    private readonly List<TimedRcloneCall> calls = [];

    public IReadOnlyList<TimedRcloneCall> Calls => calls;

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        inner.GetVersionAsync(cancellationToken);

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        inner.CreateConfigAsync(remoteName, type, parameters, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        inner.GetConfigAsync(remoteName, cancellationToken);

    private TaskCompletionSource? heldMount;
    private bool holdNextMount;

    /// <summary>The next <c>mount/mount</c> does not complete until <see cref="ReleaseMount"/>. Lets a
    /// test have a suspend arrive while <c>mount/mount</c> is already on the wire.</summary>
    public void HoldNextMount() => holdNextMount = true;

    public bool IsHoldingMount => heldMount is { Task.IsCompleted: false };

    /// <summary>The token the held <c>mount/mount</c> was called with.</summary>
    public CancellationToken MountToken { get; private set; }

    public void ReleaseMount() =>
        (heldMount ?? throw new InvalidOperationException("No mount/mount is being held.")).SetResult();

    public async Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        calls.Add(new TimedRcloneCall(time.GetUtcNow(), "mount", request.MountPoint));
        if (holdNextMount)
        {
            holdNextMount = false;
            MountToken = cancellationToken;
            heldMount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await heldMount.Task;
        }

        return await inner.MountAsync(request, cancellationToken);
    }

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        calls.Add(new TimedRcloneCall(time.GetUtcNow(), "unmount", mountPoint));
        return inner.UnmountAsync(mountPoint, cancellationToken);
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken) =>
        inner.ListMountsAsync(cancellationToken);

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        inner.ListAsync(fs, remote, cancellationToken);

    public Task<IReadOnlyList<string>> ListVfsAsync(CancellationToken cancellationToken) =>
        inner.ListVfsAsync(cancellationToken);

    public Task<RcloneVfsStats> GetVfsStatsAsync(string fs, CancellationToken cancellationToken) =>
        inner.GetVfsStatsAsync(fs, cancellationToken);

    public Task<IReadOnlyList<RcloneVfsQueueItem>> GetVfsQueueAsync(string fs, CancellationToken cancellationToken) =>
        inner.GetVfsQueueAsync(fs, cancellationToken);
}

/// <summary>
/// An <see cref="IProbe"/> that delegates to a <see cref="ProbeDouble"/>, except that it can hold
/// the next deep probe for one host open until the test releases it. That is the only way to
/// observe a host in <c>Mounting</c> (rule 2: entering <c>Mounting</c> triggers the deep probe), and
/// so the only way to have a suspend arrive while a mount is in flight.
/// </summary>
internal sealed class GatedProbe(ProbeDouble inner) : IProbe
{
    private string? gatedHostKey;
    private TaskCompletionSource<DeepProbeResult>? pending;

    /// <summary>True once the gated deep probe has been called and is waiting for
    /// <see cref="Release"/>.</summary>
    public bool IsHolding => pending is not null && !pending.Task.IsCompleted;

    /// <summary>The next deep probe for <paramref name="hostKey"/> will not complete until
    /// <see cref="Release"/> is called.</summary>
    public void HoldNextDeep(string hostKey) => gatedHostKey = hostKey;

    public void Release(DeepProbeOutcome outcome)
    {
        var tcs = pending ?? throw new InvalidOperationException("No deep probe is being held.");
        tcs.SetResult(new DeepProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == DeepProbeOutcome.Success ? null : $"gated {outcome}",
        });
    }

    public Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
        inner.ProbeShallowAsync(hostname, port, timeout, cancellationToken);

    public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (gatedHostKey is not null && string.Equals(gatedHostKey, hostKey, StringComparison.Ordinal))
        {
            gatedHostKey = null;
            pending = new TaskCompletionSource<DeepProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return pending.Task;
        }

        return inner.ProbeDeepAsync(hostKey, timeout, cancellationToken);
    }
}

/// <summary>One captured log call, stamped with the fake clock's time.</summary>
internal sealed record TimedLogEntry(DateTimeOffset At, LogLevel Level, string Message);

/// <summary>Records every log call with the fake clock's time. Locked: in the Mounting test the
/// supervisor's continuation resumes on a thread-pool thread.</summary>
internal sealed class TimedLogger(TimeProvider time) : ILogger<MountSupervisor>
{
    private readonly object gate = new();
    private readonly List<TimedLogEntry> entries = [];

    public IReadOnlyList<TimedLogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (gate)
        {
            entries.Add(new TimedLogEntry(time.GetUtcNow(), logLevel, formatter(state, exception)));
        }
    }
}
