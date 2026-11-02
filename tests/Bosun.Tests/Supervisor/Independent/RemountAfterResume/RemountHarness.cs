using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>
/// Drives a <see cref="MountSupervisor"/> for the ADR-022 (bs-hrg) independent tests. Same pump
/// strategy as <see cref="SuspendBudgetHarness"/>: enqueue, then drain the channel; time moves only
/// when a test calls <see cref="AdvanceAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two things this adds over <see cref="SuspendBudgetHarness"/>, each needed by a test here:
/// an <c>rclone rcd</c> that can stop answering <c>core/version</c> (ADR-017's gate), and a shared
/// call log into which the tests write <c>MARK|...</c> lines, so "no <c>mount/mount</c> happened
/// between the suspend and the resume" (bs-bv0) is an ordering assertion on one log rather than a
/// comparison of fake-clock times that may all be equal.
/// </para>
/// <para>
/// Nothing here reaches a real drive letter, <c>rclone rcd</c>, WinFsp, a real host, SystemEvents
/// or the real fragment path: the collaborators are in-memory doubles and a fake clock.
/// </para>
/// </remarks>
internal sealed class RemountHarness
{
    public RemountHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new ProbeDouble(Log);
        Hold = new HoldableProbe(Probe);
        Rclone = new RcloneDouble(Log);
        Rc = new VersionGatedRclone(Rclone, Log);
        Logger = new TimedLogger(Time);
        Store = new ReloadableConfigStoreDouble(config);
        Supervisor = new MountSupervisor(
            Store, Rc, Hold, new Bosun.Tests.Supervisor.Fakes.FakeDriveTypeReader(), Time, Logger);
    }

    public FakeTimeProvider Time { get; }

    public SupervisorCallLog Log { get; } = new();

    public ProbeDouble Probe { get; }

    /// <summary>Wraps <see cref="Probe"/>; can hold one shallow or one deep probe open so a test can
    /// act while the supervisor is mid-probe.</summary>
    public HoldableProbe Hold { get; }

    public RcloneDouble Rclone { get; }

    public VersionGatedRclone Rc { get; }

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

    /// <summary>Suspend, with a <c>MARK|suspend</c> line written to the call log first.</summary>
    public Task SuspendAsync()
    {
        Log.Record("MARK|suspend");
        return RunAsync(() => Supervisor.SuspendAsync());
    }

    /// <summary>Resume, with a <c>MARK|resume</c> line written to the call log first.</summary>
    public Task ResumeAsync()
    {
        Log.Record("MARK|resume");
        return RunAsync(() => Supervisor.ResumeAsync());
    }

    public Task MountAsync(string hostKey) => RunAsync(() => Supervisor.RequestMountAsync(hostKey));

    public Task UnmountAsync(string hostKey) => RunAsync(() => Supervisor.RequestUnmountAsync(hostKey));

    public Task ReloadAsync(BosunConfig next)
    {
        Store.Publish(next);
        return RunAsync(() => Supervisor.ConfigChangedAsync(next));
    }

    /// <summary>Steps the clock (1 ms steps up to a minute, 1 s beyond), pumping after each step.
    /// See <see cref="SuspendBudgetHarness.AdvanceAsync"/> for why stepping matters.</summary>
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

    /// <summary><paramref name="hostKey"/>'s transitions, OLDEST FIRST.</summary>
    public IReadOnlyList<MountTransitionEntry> Transitions(string hostKey) =>
        Supervisor.GetTransitionHistory().Reverse().Where(t => t.HostKey == hostKey).ToList();

    /// <summary>Index in the call log of the LAST line equal to <paramref name="marker"/>.</summary>
    public int LastMarkIndex(string marker)
    {
        var entries = Log.Entries;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] == marker)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no '{marker}' in the call log");
    }

    /// <summary>Call-log lines after index <paramref name="from"/> (exclusive).</summary>
    public IReadOnlyList<string> LogAfter(int from) => Log.Entries.Skip(from + 1).ToList();

    public string TransitionDump() => string.Join(
        Environment.NewLine,
        Supervisor.GetTransitionHistory().Reverse().Select(t => $"{t.HostKey}: {t.From} -> {t.To} ({t.Trigger}; {t.DrainCause})"));

    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";
}

/// <summary>
/// Delegates to an <see cref="RcloneDouble"/> (whose mount table stays the single source of truth)
/// and adds an <c>rclone rcd</c> that stops answering <c>core/version</c> while
/// <see cref="VersionFails"/> is set. Logs every version call to the shared call log.
/// </summary>
internal sealed class VersionGatedRclone(RcloneDouble inner, SupervisorCallLog log) : IRcloneClient
{
    public bool VersionFails { get; set; }

    public int VersionCalls { get; private set; }

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        VersionCalls++;
        if (VersionFails)
        {
            log.Record("version|failed");
            return Task.FromException<RcloneVersionInfo>(new HttpRequestException("rclone rcd is not answering core/version"));
        }

        log.Record("version|ok");
        return inner.GetVersionAsync(cancellationToken);
    }

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        inner.CreateConfigAsync(remoteName, type, parameters, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        inner.GetConfigAsync(remoteName, cancellationToken);

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken) =>
        inner.MountAsync(request, cancellationToken);

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken) =>
        inner.UnmountAsync(mountPoint, cancellationToken);

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
/// the next shallow probe for one hostname, or the next deep probe for one host key, open until the
/// test releases it. The supervisor's channel is serialised, so while a probe is held the command
/// that issued it is parked mid-await and anything the test enqueues runs after it -- which is
/// exactly the interleaving "a suspend arrives while the first post-resume probe is in flight".
/// </summary>
internal sealed class HoldableProbe(ProbeDouble inner) : IProbe
{
    private string? heldShallowHost;
    private string? heldDeepHost;
    private TaskCompletionSource<ShallowProbeResult>? pendingShallow;
    private TaskCompletionSource<DeepProbeResult>? pendingDeep;

    public bool IsHoldingShallow => pendingShallow is not null && !pendingShallow.Task.IsCompleted;

    public bool IsHoldingDeep => pendingDeep is not null && !pendingDeep.Task.IsCompleted;

    public void HoldNextShallow(string hostname) => heldShallowHost = hostname;

    public void HoldNextDeep(string hostKey) => heldDeepHost = hostKey;

    public void ReleaseShallow(ShallowProbeOutcome outcome) =>
        (pendingShallow ?? throw new InvalidOperationException("No shallow probe is being held.")).SetResult(
            new ShallowProbeResult
            {
                Outcome = outcome,
                Elapsed = TimeSpan.Zero,
                Detail = outcome == ShallowProbeOutcome.Success ? null : $"held {outcome}",
            });

    public void ReleaseDeep(DeepProbeOutcome outcome) =>
        (pendingDeep ?? throw new InvalidOperationException("No deep probe is being held.")).SetResult(
            new DeepProbeResult
            {
                Outcome = outcome,
                Elapsed = TimeSpan.Zero,
                Detail = outcome == DeepProbeOutcome.Success ? null : $"held {outcome}",
            });

    public Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (heldShallowHost is not null && string.Equals(heldShallowHost, hostname, StringComparison.OrdinalIgnoreCase))
        {
            heldShallowHost = null;
            pendingShallow = new TaskCompletionSource<ShallowProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return pendingShallow.Task;
        }

        return inner.ProbeShallowAsync(hostname, port, timeout, cancellationToken);
    }

    public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (heldDeepHost is not null && string.Equals(heldDeepHost, hostKey, StringComparison.Ordinal))
        {
            heldDeepHost = null;
            pendingDeep = new TaskCompletionSource<DeepProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return pendingDeep.Task;
        }

        return inner.ProbeDeepAsync(hostKey, timeout, cancellationToken);
    }
}
