using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// Drives a <see cref="MountSupervisor"/> for the ADR-023 (bs-4in, bs-7d7, bs-4ca) tests in this folder,
/// written from ADR-023 and docs/ARCHITECTURE.md §3/§4/§5 rather than from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a "hung" check is made.</b> A <see cref="Hold"/> captures the next matching probe or rc call.
/// The test fires the timer (<see cref="FireTimers"/>, which moves the fake clock without pumping), then
/// starts the pump (<see cref="StartPump"/>); the pump runs until it awaits the held call and returns an
/// incomplete task. While it is stuck, the test calls <c>SuspendAsync</c> on its own thread -- which is
/// exactly production's shape: the channel's consumer is blocked on I/O and the suspend arrives on another
/// thread (<c>SystemEvents</c>' notification thread).
/// </para>
/// <para>
/// <b>Guards, not sleeps.</b> <see cref="FinishAsync"/> waits for the pump with a real-time guard. No
/// assertion depends on how long anything takes; the guard only turns "the suspend waited for a hung call
/// that nothing will ever release" into a failure with a message instead of a hung test run.
/// </para>
/// <para>
/// Nothing here reaches a real drive letter, <c>rclone rcd</c>, WinFsp, <c>SystemEvents</c>, a real host,
/// or the fragment path. Time moves only when a test moves it.
/// </para>
/// </remarks>
internal sealed class PreemptSpecHarness
{
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    public PreemptSpecHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new ProbeDouble(Log);
        HProbe = new HoldableProbe(Probe);
        Rclone = new RcloneDouble(Log);
        HRclone = new HoldableRclone(Rclone, Time);
        Logger = new TimedLogger(Time);
        Store = new ReloadableConfigStoreDouble(config);
        Supervisor = new MountSupervisor(
            Store, HRclone, HProbe, new Bosun.Tests.Supervisor.Fakes.FakeDriveTypeReader(), Time, Logger);
    }

    public FakeTimeProvider Time { get; }

    public SupervisorCallLog Log { get; } = new();

    /// <summary>Scripting for every probe that is not held.</summary>
    public ProbeDouble Probe { get; }

    public HoldableProbe HProbe { get; }

    /// <summary>The in-memory mount table: what Explorer would see.</summary>
    public RcloneDouble Rclone { get; }

    public HoldableRclone HRclone { get; }

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

    public Task SuspendAsync() => RunAsync(() => SuspendCaller.Call(Supervisor));

    public Task ResumeAsync() => RunAsync(() => Supervisor.ResumeAsync());

    /// <summary>Moves the fake clock in steps, pumping after each one (see
    /// <c>SuspendBudgetHarness.AdvanceAsync</c> for why stepping matters).</summary>
    public async Task AdvanceAsync(TimeSpan delta, TimeSpan? step = null)
    {
        var size = step ?? (delta <= TimeSpan.FromMinutes(1) ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1));
        var target = Now + delta;
        while (Now < target)
        {
            var next = target - Now < size ? target - Now : size;
            Time.Advance(next);
            await Supervisor.DrainAsync();
        }

        await Supervisor.DrainAsync();
    }

    public Task AdvanceSecondsAsync(double seconds) => AdvanceAsync(TimeSpan.FromSeconds(seconds));

    /// <summary>Moves the fake clock WITHOUT pumping: due timers enqueue their work and nothing runs it
    /// until <see cref="StartPump"/>.</summary>
    public void FireTimers(TimeSpan delta) => Time.Advance(delta);

    /// <summary>Starts pumping the channel. Returns as soon as the pump awaits something that is not
    /// complete (a held call), or when the channel is empty.</summary>
    public Task StartPump() => Supervisor.DrainAsync();

    /// <summary>
    /// Waits for <paramref name="pump"/> (with the guard), then pumps until each of
    /// <paramref name="pending"/> has completed. The second pump picks up work a concurrent caller
    /// enqueued after the first pump had already found the channel empty.
    /// </summary>
    public async Task FinishAsync(Task pump, string whatWouldHang, params Task[] pending)
    {
        try
        {
            await pump.WaitAsync(Guard);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"The supervisor's channel stayed blocked: {whatWouldHang}");
        }

        await Supervisor.DrainAsync();

        // Completion only: a call that faults (a refused mount request, say) is the test's to inspect.
        foreach (var task in pending)
        {
            await Task.WhenAny(task, Task.Delay(Guard));
            Assert.True(task.IsCompleted, $"A supervisor call never completed: {whatWouldHang}");
        }
    }

    /// <summary>Waits (guarded) for a held call to be reached.</summary>
    /// <summary>
    /// Yields until <paramref name="condition"/> holds, failing with <paramref name="message"/> after
    /// <see cref="Guard"/>. For a held call's <see cref="Hold.TokenCancelled"/>: <c>CancelAsync</c>
    /// (ADR-023 decision 2) cancels the tick's own token at once but reaches the token linked into
    /// the held call only when the thread pool runs its callbacks (bs-3jd).
    /// </summary>
    public static async Task WaitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message);
            await Task.Yield();
        }
    }

    public static async Task ArrivedAsync(Hold hold, string what)
    {
        try
        {
            await hold.WhenArrived.WaitAsync(Guard);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"setup: the held call was never reached: {what}");
        }
    }

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    /// <summary><paramref name="hostKey"/>'s transitions, oldest first.</summary>
    public IReadOnlyList<MountTransitionEntry> Transitions(string hostKey) =>
        Supervisor.GetTransitionHistory().Reverse().Where(t => t.HostKey == hostKey).ToList();

    /// <summary>The cause of every drain <paramref name="hostKey"/> has begun, oldest first.</summary>
    public IReadOnlyList<DrainCause> DrainCauses(string hostKey) =>
        Transitions(hostKey).Where(t => t.To == MountState.Draining).Select(t => t.DrainCause!.Value).ToList();

    public int MountCalls(string drive) => HRclone.Count("mount", drive);

    public int TotalMountCalls => HRclone.Count("mount");

    public int ShallowCalls(string hostKey) => HProbe.ShallowCalls(HostnameOf(hostKey));

    public bool IsMountedInRclone(string drive) => Rclone.FsAt(drive) is not null;

    /// <summary>Error-level log lines. A cancellation that escapes a preempted tick reaches the
    /// channel's catch-all, which logs at Error ("Unhandled exception processing a supervisor
    /// action") -- and an action that ended that way did not re-arm its timer (ADR-011).</summary>
    public IReadOnlyList<TimedLogEntry> Errors => Logger.Entries.Where(e => e.Level >= LogLevel.Error).ToList();

    /// <summary>Fake-clock times the supervisor logged escalating <paramref name="hostKey"/>'s drain to
    /// a forced unmount (same observation as <c>SuspendBudgetHarness.EscalationTimes</c>).</summary>
    public IReadOnlyList<DateTimeOffset> EscalationTimes(string hostKey) =>
        Logger.Entries
            .Where(e => e.Level == LogLevel.Warning
                        && e.Message.Contains(hostKey, StringComparison.Ordinal)
                        && e.Message.Contains("escalat", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.At)
            .ToList();

    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";
}
