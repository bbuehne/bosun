using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>
/// The result of running one supervisor command with the test pump. Each field is a different
/// way bs-x57's bug shows up.
/// </summary>
/// <param name="What">The command, for failure messages.</param>
/// <param name="Completed">Whether the command's task finished.</param>
/// <param name="OperationFault">What the command's task threw to its caller, if anything.</param>
/// <param name="PumpFault">What escaped the channel pump itself. In production the pump is
/// <c>RunAsync</c>, the supervisor's only consumer. An exception escaping it means the loop has
/// died and no host will be processed again.</param>
internal sealed record PumpOutcome(string What, bool Completed, Exception? OperationFault, Exception? PumpFault)
{
    public void AssertClean()
    {
        if (PumpFault is not null)
        {
            Assert.Fail(
                $"{What}: {Describe(PumpFault)} escaped the supervisor's channel pump. In production the pump is " +
                "RunAsync, the single channel consumer: an exception escaping it ends the loop, and every host " +
                "goes permanently silent (bs-x57).");
        }

        if (!Completed)
        {
            Assert.Fail($"{What} never completed: the work it queued was never processed.");
        }

        if (OperationFault is not null)
        {
            Assert.Fail(
                $"{What} threw {Describe(OperationFault)} to its caller. An rc failure must be handled inside the " +
                "supervisor like any other rc failure, not passed up as if the supervisor were shutting down (bs-x57).");
        }
    }

    public static string Describe(Exception ex) => $"{ex.GetType().Name} (\"{ex.Message}\")";
}

/// <summary>
/// Wires a <see cref="MountSupervisor"/> to <see cref="FaultInjectingRcloneDouble"/>,
/// <see cref="FaultableProbe"/>, a <see cref="FakeTimeProvider"/> and
/// <see cref="LevelRecordingLogger{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the same pump strategy as the existing supervisor harnesses: queue the work, then call
/// <c>DrainAsync</c>. The difference is that this harness <b>records</b> what escapes the pump
/// instead of letting it end the test. The bug under test is an exception escaping the pump, so
/// each test has to see that happen and fail with a message that names it.
/// </para>
/// <para>
/// <see cref="AssertInvariants"/> checks bs-x57's item 4 after every advance step:
/// <c>Mounting</c> is entered only from <c>Ready</c> (Invariant I1), and every
/// <c>Draining -&gt; Disabled</c> happened only after <c>mount/listmounts</c> showed the drive
/// gone, at a moment when the drive really was gone (§4 rule 4).
/// </para>
/// <para>
/// Nothing here touches a real rclone, WinFsp, a drive letter, or the real clock.
/// </para>
/// </remarks>
internal sealed class RcTimeoutHarness
{
    private readonly IReadOnlyDictionary<string, string?> drives;

    public RcTimeoutHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Rclone = new FaultInjectingRcloneDouble(Time);
        Probe = new FaultableProbe(new ProbeDouble(new SupervisorCallLog()));
        Logger = new LevelRecordingLogger<MountSupervisor>();
        Supervisor = new MountSupervisor(new FakeHostConfigStore(config), Rclone, Probe, Time, Logger);
        Rclone.TransitionCount = () => Supervisor.GetTransitionHistory().Count;
        drives = config.Hosts.ToDictionary(h => h.Key, h => h.Value.Mount.Drive, StringComparer.Ordinal);
    }

    public FakeTimeProvider Time { get; }

    public FaultInjectingRcloneDouble Rclone { get; }

    public FaultableProbe Probe { get; }

    public LevelRecordingLogger<MountSupervisor> Logger { get; }

    public MountSupervisor Supervisor { get; }

    /// <summary>Starts <paramref name="operation"/>, pumps the channel until empty, and reports
    /// what happened without throwing.</summary>
    public async Task<PumpOutcome> TryRunAsync(string what, Func<Task> operation)
    {
        var task = operation();
        var pumpFault = await Record.ExceptionAsync(() => Supervisor.DrainAsync());
        Exception? operationFault = null;
        if (task.IsCompleted)
        {
            operationFault = await Record.ExceptionAsync(() => task);
        }

        return new PumpOutcome(what, task.IsCompleted, operationFault, pumpFault);
    }

    public async Task RunCleanAsync(string what, Func<Task> operation) =>
        (await TryRunAsync(what, operation)).AssertClean();

    public Task StartCleanAsync() => RunCleanAsync("StartAsync", () => Supervisor.StartAsync());

    /// <summary>Advances the fake clock (firing due timers, which queue work), then pumps.
    /// Returns whatever escaped the pump.</summary>
    public async Task<Exception?> TryAdvanceAsync(TimeSpan delta)
    {
        Time.Advance(delta);
        return await Record.ExceptionAsync(() => Supervisor.DrainAsync());
    }

    public async Task AdvanceCleanAsync(TimeSpan delta, string context)
    {
        var fault = await TryAdvanceAsync(delta);
        new PumpOutcome($"{context} (clock at +{Time.GetUtcNow() - DateTimeOffset.UnixEpoch})", true, null, fault)
            .AssertClean();
    }

    /// <summary>Advances in <paramref name="step"/> increments until
    /// <paramref name="condition"/> holds or <paramref name="max"/> has passed. Every step must
    /// pump cleanly and keep <see cref="AssertInvariants"/>. Returns whether the condition was
    /// met.</summary>
    public async Task<bool> AdvanceUntilAsync(Func<bool> condition, TimeSpan step, TimeSpan max, string context)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < max; elapsed += step)
        {
            await AdvanceCleanAsync(step, context);
            AssertInvariants();
            if (condition())
            {
                return true;
            }
        }

        return false;
    }

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    /// <summary>The transition history, oldest first. <c>GetTransitionHistory</c> returns
    /// newest first.</summary>
    public IReadOnlyList<MountTransitionEntry> HistoryOldestFirst() =>
        Supervisor.GetTransitionHistory().Reverse().ToList();

    public int TransitionCount(string hostKey, MountState from, MountState to) =>
        HistoryOldestFirst().Count(t => t.HostKey == hostKey && t.From == from && t.To == to);

    public string DumpHistory() =>
        string.Join(Environment.NewLine, HistoryOldestFirst().Select(t =>
            $"  +{t.TimestampUtc - DateTimeOffset.UnixEpoch} {t.HostKey}: {t.From} -> {t.To} ({t.Trigger})"));

    public string DumpCalls() =>
        string.Join(Environment.NewLine, Rclone.Calls.Select(c =>
            $"  #{c.Sequence} +{c.At - DateTimeOffset.UnixEpoch} {c.Operation} {c.MountPoint}" +
            $"{(c.Faulted ? " FAULTED" : string.Empty)}" +
            $"{(c.Listed is null ? string.Empty : $" -> [{string.Join(',', c.Listed)}]")}" +
            $" (after {c.TransitionsBefore} transitions)"));

    /// <summary>
    /// bs-x57 item 4. (a) <c>Mounting</c> is entered only from <c>Ready</c>. (b) Each
    /// <c>Draining -&gt; Disabled</c> was preceded by a successful <c>mount/listmounts</c> that
    /// did not list the host's drive, and the drive really was absent from the fake's mount table
    /// at that moment.
    /// </summary>
    public void AssertInvariants()
    {
        var history = HistoryOldestFirst();
        Assert.True(
            history.Count < MountSupervisor.TransitionHistoryCapacity,
            "Test produced enough transitions to fill the history ring buffer. The ordering checks below " +
            "rely on the count growing by one per transition, so this test must be made shorter.");

        var calls = Rclone.Calls;

        for (var i = 0; i < history.Count; i++)
        {
            var t = history[i];

            if (t.To == MountState.Mounting && t.From != MountState.Ready)
            {
                Assert.Fail(
                    $"Invariant I1 / §4 rule 1: {t.HostKey} entered Mounting from {t.From} " +
                    $"(trigger: {t.Trigger}). Mounting is reachable only from Ready.{Environment.NewLine}{DumpHistory()}");
            }

            if (t.From != MountState.Draining || t.To != MountState.Disabled)
            {
                continue;
            }

            var drive = drives.TryGetValue(t.HostKey, out var d) ? d : null;
            if (drive is null)
            {
                continue;
            }

            var before = calls.Where(c => c.TransitionsBefore <= i).ToList();

            var lastEffect = before.LastOrDefault(c =>
                c.Operation is FaultInjectingRcloneDouble.EffectMounted or FaultInjectingRcloneDouble.EffectUnmounted &&
                string.Equals(c.MountPoint, drive, StringComparison.OrdinalIgnoreCase));
            if (lastEffect?.Operation == FaultInjectingRcloneDouble.EffectMounted)
            {
                Assert.Fail(
                    $"§4 rule 4: {t.HostKey} went Draining -> Disabled at +{t.TimestampUtc - DateTimeOffset.UnixEpoch} " +
                    $"while {drive} was STILL MOUNTED in rclone. The state machine believes a drive is gone when it is " +
                    $"not.{Environment.NewLine}History:{Environment.NewLine}{DumpHistory()}{Environment.NewLine}" +
                    $"rc calls:{Environment.NewLine}{DumpCalls()}");
            }

            var lastList = before.LastOrDefault(c => c.Operation == FaultInjectingRcloneDouble.ListMounts);
            var confirmed = lastList is { Faulted: false, Listed: not null } &&
                !lastList.Listed.Contains(drive, StringComparer.OrdinalIgnoreCase);
            if (!confirmed)
            {
                Assert.Fail(
                    $"§4 rule 4: {t.HostKey} went Draining -> Disabled without mount/listmounts confirming {drive} gone " +
                    $"(last listmounts before the transition: {(lastList is null ? "none" : lastList.Faulted ? "FAULTED" : $"[{string.Join(',', lastList.Listed!)}]")})." +
                    $"{Environment.NewLine}History:{Environment.NewLine}{DumpHistory()}{Environment.NewLine}" +
                    $"rc calls:{Environment.NewLine}{DumpCalls()}");
            }
        }
    }
}
