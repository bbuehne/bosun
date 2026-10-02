using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor.Independent.RepairAll;

/// <summary>
/// Drives a real <see cref="MountSupervisor"/> against <see cref="RecordingProbe"/>,
/// <see cref="RepairRcloneDouble"/> and a <see cref="FakeTimeProvider"/>, for the bs-aoz
/// "Unmount all &amp; re-probe" tests.
/// </summary>
/// <remarks>
/// <para>
/// Same pump strategy as the other supervisor harnesses: queue the work, then call the
/// supervisor's internal <c>DrainAsync</c>. Unlike <see cref="IndependentHarness"/>, an exception
/// escaping the pump is caught and reported as a failure that names it. In production the pump is
/// the supervisor's only channel consumer, so an exception there is a dead loop (bs-x57), and
/// spec item 9 of bs-aoz is that a failing rc call during a repair must not cause one.
/// </para>
/// <para>
/// <see cref="AssertInvariants"/> is meant to be called after every step. It checks bs-aoz's
/// invariants over the whole transition history: Mounting is entered only from Ready (I1);
/// Mounted leaves only for Draining; and a host leaves Draining only after a successful
/// <c>mount/listmounts</c> that did not list its drive, at a moment the drive really was gone
/// (§4 rule 4).
/// </para>
/// <para>
/// Nothing here touches a real rclone, WinFsp, drive letter, SFTP host, or the wall clock.
/// </para>
/// </remarks>
internal sealed class RepairHarness
{
    private readonly IReadOnlyDictionary<string, string?> drives;

    public RepairHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Log = new RepairEventLog();
        Probe = new ProbeDouble(new SupervisorCallLog());
        Rclone = new RepairRcloneDouble(Log);
        Supervisor = new MountSupervisor(
            new ReloadableConfigStoreDouble(config),
            Rclone,
            new RecordingProbe(Probe, Log),
            Time,
            NullLogger<MountSupervisor>.Instance);
        Log.TransitionCount = () => Supervisor.GetTransitionHistory().Count;
        drives = config.Hosts.ToDictionary(h => h.Key, h => h.Value.Mount.Drive, StringComparer.Ordinal);
    }

    public FakeTimeProvider Time { get; }

    public RepairEventLog Log { get; }

    /// <summary>The scripting surface for probe outcomes. Shallow outcomes are keyed by hostname
    /// (<see cref="HostnameOf"/>), deep outcomes by host key.</summary>
    public ProbeDouble Probe { get; }

    public RepairRcloneDouble Rclone { get; }

    public MountSupervisor Supervisor { get; }

    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";

    public Task StartAsync() => RunAsync("StartAsync", () => Supervisor.StartAsync());

    /// <summary>Runs <paramref name="operation"/>, pumps everything it queued, and fails the test
    /// if the pump faulted, the operation never completed, or the operation threw.</summary>
    public async Task RunAsync(string what, Func<Task> operation)
    {
        var task = operation();
        var pumpFault = await Record.ExceptionAsync(() => Supervisor.DrainAsync());
        if (pumpFault is not null)
        {
            Assert.Fail($"{what}: {Describe(pumpFault)} escaped the supervisor's channel pump -- in production that " +
                        $"is the loop dying (bs-x57).{Environment.NewLine}{Dump()}");
        }

        if (!task.IsCompleted)
        {
            Assert.Fail($"{what} never completed after its queued work was pumped.{Environment.NewLine}{Dump()}");
        }

        var operationFault = await Record.ExceptionAsync(() => task);
        if (operationFault is not null)
        {
            Assert.Fail($"{what} threw {Describe(operationFault)} to its caller.{Environment.NewLine}{Dump()}");
        }

        AssertInvariants();
    }

    /// <summary>
    /// "Unmount all &amp; re-probe", then lets any zero-delay timer it armed fire. That is the
    /// meaning of "promptly" / "at once" in these tests: no clock advance at all.
    /// </summary>
    public async Task RepairAsync()
    {
        await RunAsync("RepairAllAsync", () => Supervisor.RepairAllAsync());
        await AdvanceAsync(TimeSpan.Zero);
    }

    /// <summary>Moves the clock, pumps, and checks the invariants.</summary>
    public async Task AdvanceAsync(TimeSpan delta)
    {
        Time.Advance(delta);
        var pumpFault = await Record.ExceptionAsync(() => Supervisor.DrainAsync());
        if (pumpFault is not null)
        {
            Assert.Fail($"advancing to +{Elapsed}: {Describe(pumpFault)} escaped the supervisor's channel pump -- in " +
                        $"production that is the loop dying (bs-x57).{Environment.NewLine}{Dump()}");
        }

        AssertInvariants();
    }

    public Task AdvanceSecondsAsync(int seconds) => AdvanceAsync(TimeSpan.FromSeconds(seconds));

    /// <summary>Advances in one-second steps for <paramref name="seconds"/>, checking the
    /// invariants and <paramref name="eachStep"/> after every step.</summary>
    public async Task StepSecondsAsync(int seconds, Action? eachStep = null)
    {
        for (var i = 0; i < seconds; i++)
        {
            await AdvanceSecondsAsync(1);
            eachStep?.Invoke();
        }
    }

    /// <summary>Advances one second at a time until <paramref name="condition"/> holds, or
    /// <paramref name="maxSeconds"/> have passed. Returns whether it held.</summary>
    public async Task<bool> AdvanceUntilAsync(Func<bool> condition, int maxSeconds)
    {
        for (var i = 0; i < maxSeconds; i++)
        {
            if (condition())
            {
                return true;
            }

            await AdvanceSecondsAsync(1);
        }

        return condition();
    }

    public TimeSpan Elapsed => Time.GetUtcNow() - DateTimeOffset.UnixEpoch;

    public HostMountSnapshot Snapshot(string hostKey) => Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    public IReadOnlyList<MountTransitionEntry> History() => Supervisor.GetTransitionHistory().Reverse().ToList();

    /// <summary>Transitions recorded at or after index <paramref name="from"/> (oldest first).</summary>
    public IReadOnlyList<MountTransitionEntry> HistorySince(int from) => History().Skip(from).ToList();

    public IReadOnlyList<(MountState From, MountState To)> PathSince(int from, string hostKey) =>
        HistorySince(from).Where(t => t.HostKey == hostKey).Select(t => (t.From, t.To)).ToList();

    public int MountCalls(string drive) =>
        Log.Events.Count(e => e.Kind == RepairEventLog.Mount && string.Equals(e.Subject, drive, StringComparison.OrdinalIgnoreCase));

    public int ShallowProbes(string hostKey) =>
        Log.Events.Count(e => e.Kind == RepairEventLog.Shallow && e.Subject == hostKey);

    public string Dump(long afterSequence = 0) =>
        $"Transitions:{Environment.NewLine}" +
        string.Join(Environment.NewLine, History().Select((t, i) =>
            $"  [{i}] +{t.TimestampUtc - DateTimeOffset.UnixEpoch} {t.HostKey}: {t.From} -> {t.To} ({t.Trigger})")) +
        $"{Environment.NewLine}Calls:{Environment.NewLine}{Log.Dump(afterSequence)}";

    /// <summary>bs-aoz spec item 7, checked over the whole history so far.</summary>
    public void AssertInvariants()
    {
        var history = History();
        Assert.True(
            history.Count < MountSupervisor.TransitionHistoryCapacity,
            "This test produced enough transitions to fill the history ring buffer; the ordering checks rely on " +
            "the count growing by one per transition, so the test must be made shorter.");

        var events = Log.Events;

        for (var i = 0; i < history.Count; i++)
        {
            var t = history[i];

            if (t.To == MountState.Mounting && t.From != MountState.Ready)
            {
                Assert.Fail($"I1 / §4 rule 1: [{i}] {t.HostKey} entered Mounting from {t.From}.{Environment.NewLine}{Dump()}");
            }

            if (t.From == MountState.Mounted && t.To != MountState.Draining)
            {
                Assert.Fail($"§4 diagram: [{i}] {t.HostKey} left Mounted for {t.To}; Mounted leaves only for Draining." +
                            $"{Environment.NewLine}{Dump()}");
            }

            if (t.From != MountState.Draining)
            {
                continue;
            }

            if (t.To != MountState.Disabled)
            {
                Assert.Fail($"§4 diagram: [{i}] {t.HostKey} left Draining for {t.To}; Draining's only exit is Disabled, " +
                            $"on unmount confirmed.{Environment.NewLine}{Dump()}");
            }

            if (!drives.TryGetValue(t.HostKey, out var drive) || drive is null)
            {
                continue;
            }

            var before = events.Where(e => e.TransitionsBefore <= i).ToList();

            var lastEffect = before.LastOrDefault(e =>
                e.Kind is RepairEventLog.EffectMounted or RepairEventLog.EffectUnmounted &&
                string.Equals(e.Subject, drive, StringComparison.OrdinalIgnoreCase));
            if (lastEffect?.Kind == RepairEventLog.EffectMounted)
            {
                Assert.Fail($"§4 rule 4: [{i}] {t.HostKey} went Draining -> Disabled while {drive} was still mounted in " +
                            $"rclone. The supervisor believes a drive is gone when it is not.{Environment.NewLine}{Dump()}");
            }

            var lastList = before.LastOrDefault(e => e.Kind == RepairEventLog.ListMounts);
            var confirmed = lastList is { Ok: true, Listed: not null } &&
                            !lastList.Listed.Contains(drive, StringComparer.OrdinalIgnoreCase);
            if (!confirmed)
            {
                Assert.Fail($"§4 rule 4: [{i}] {t.HostKey} went Draining -> Disabled without a mount/listmounts confirming " +
                            $"{drive} gone (last listmounts before it: " +
                            $"{(lastList is null ? "none" : lastList.Ok ? $"[{string.Join(',', lastList.Listed!)}]" : "FAILED")})." +
                            $"{Environment.NewLine}{Dump()}");
            }
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name} (\"{ex.Message}\")";
}
