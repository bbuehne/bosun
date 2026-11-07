using System.Text.RegularExpressions;
using Bosun.Configuration;
using Bosun.Repair;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Repair;

/// <summary>
/// bs-5ga.2, the click's side: what "Unmount all &amp; re-probe" tells the user when a pending suspend stopped the
/// repair partway. Written from the note in ADR-023's bs-5ga.1 amendment and docs/ARCHITECTURE.md §4 rule 5, not
/// from the implementation or the implementer's tests.
/// </summary>
/// <remarks>
/// <para>The spec quotes these tests rest on:</para>
/// <list type="bullet">
/// <item>"<c>RepairCommands</c> shows that in the same dialog, with the same fail-soft path, as a refused repair
/// (<c>RepairRefusedException</c>, <c>bs-mhs</c>), worded so it does not say "nothing was unmounted" (some of it
/// ran)" (ADR-023).</item>
/// <item>"the click shows "stopped: the computer is going to sleep", as a refused repair shows its reason.
/// "Refused" ... and "stopped" (partway) stay distinct" (§4 rule 5).</item>
/// </list>
/// <para>
/// The supervisor here is a stub that answers <c>RepairAllAsync</c> as scripted; the last test drives a real
/// <see cref="MountSupervisor"/> over fakes instead. No dialog is shown, no real rclone, drive letter or host is
/// reached.
/// </para>
/// </remarks>
public sealed class RepairStoppedReportSpecTests
{
    private const string Title = "Unmount all & re-probe";
    private const string SleepReason = "the computer is going to sleep";

    private static (RepairCommands Commands, ScriptedRepairSupervisor Supervisor, RecordingRepairPrompt Prompt, RecordingLogger<RepairCommands> Log)
        NewCommands(Func<CancellationToken, Task> repair)
    {
        var supervisor = new ScriptedRepairSupervisor(repair);
        var prompt = new RecordingRepairPrompt();
        var log = new RecordingLogger<RepairCommands>();
        var commands = new RepairCommands(supervisor, () => null, () => null, prompt, new FakeTimeProvider(), logger: log);
        return (commands, supervisor, prompt, log);
    }

    private static Task Stopped(CancellationToken _) => Task.FromException(new RepairStoppedException(SleepReason));

    /// <summary>
    /// <b>Protects:</b> the click is told the repair stopped, in the one dialog, with the reason.
    /// <b>Catches:</b> <c>RepairStoppedException</c> not handled, so it reaches the generic failure path ("Bosun could
    /// not unmount and re-probe the hosts" plus the exception text, logged as an Error with a stack), or nothing
    /// shown at all (the old silent success).
    /// </summary>
    [Fact]
    public async Task A_stopped_repair_shows_one_dialog_naming_the_stop_and_its_reason()
    {
        var (commands, _, prompt, log) = NewCommands(Stopped);

        await commands.UnmountAllAsync();

        var shown = Assert.Single(prompt.Errors);
        Assert.Equal(Title, shown.Title);
        Assert.Contains("stopped", shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SleepReason, shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not", shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }

    /// <summary>
    /// <b>Protects:</b> "worded so it does not say "nothing was unmounted" (some of it ran)".
    /// <b>Catches:</b> the stop routed through the refusal's message, which says "Nothing was unmounted" about a
    /// repair that did unmount drives: a false statement the user would act on.
    /// </summary>
    [Fact]
    public async Task A_stopped_repairs_message_never_says_nothing_was_unmounted()
    {
        var (commands, _, prompt, _) = NewCommands(Stopped);

        await commands.UnmountAllAsync();

        var shown = Assert.Single(prompt.Errors);
        Assert.DoesNotMatch(new Regex(@"nothing\s+(was|has been)\s+(un)?mounted", RegexOptions.IgnoreCase), shown.Message);
        Assert.DoesNotMatch(new Regex(@"\bno\s+drives?\s+(was|were)\s+unmounted", RegexOptions.IgnoreCase), shown.Message);
    }

    /// <summary>
    /// <b>Protects:</b> the message is factual about the machine and does not blame the user. The machine going to
    /// sleep is not the user's mistake, and the message must not tell them to have done anything differently.
    /// <b>Catches:</b> wording such as "you put the computer to sleep" or "try again and do not ..." creeping into
    /// a message that reports a system event.
    /// </summary>
    [Fact]
    public async Task A_stopped_repairs_message_does_not_blame_the_user()
    {
        var (commands, _, prompt, _) = NewCommands(Stopped);

        await commands.UnmountAllAsync();

        var shown = Assert.Single(prompt.Errors);
        Assert.DoesNotMatch(
            new Regex(@"\byou\s+(put|sent|closed|slept|suspended|caused|interrupted|should|must|need)\b|\bbecause\s+you\b|\byour\s+fault\b|\buser\s+error\b", RegexOptions.IgnoreCase),
            shown.Message);
    }

    /// <summary>
    /// <b>Protects:</b> "refused" and "stopped" stay distinct, and the refusal is unchanged: it still says nothing
    /// was unmounted (true for a refusal), and the two messages differ.
    /// <b>Catches:</b> the two exceptions folded into one handler with one message, either the refusal losing
    /// "nothing was unmounted" or the stop gaining it.
    /// </summary>
    [Fact]
    public async Task A_refused_repair_still_says_nothing_was_unmounted_and_differs_from_a_stopped_one()
    {
        var (refusedCommands, _, refusedPrompt, _) = NewCommands(
            _ => Task.FromException(new RepairRefusedException("the system is suspended")));
        var (stoppedCommands, _, stoppedPrompt, _) = NewCommands(Stopped);

        await refusedCommands.UnmountAllAsync();
        await stoppedCommands.UnmountAllAsync();

        var refused = Assert.Single(refusedPrompt.Errors);
        var stopped = Assert.Single(stoppedPrompt.Errors);
        Assert.Equal(Title, refused.Title);
        Assert.Contains("nothing was unmounted", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the system is suspended", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(refused.Message, stopped.Message);
    }

    /// <summary>
    /// <b>Protects:</b> "the same fail-soft path" and the click-handler contract that repairs never throw: a
    /// dialog that cannot be shown does not turn a stop into an exception out of a click handler.
    /// <b>Catches:</b> the stop's dialog shown outside the fail-soft wrapper, so a prompt failure (the main window
    /// being torn down as the machine goes to sleep) escapes the fire-and-forget click.
    /// </summary>
    [Fact]
    public async Task A_stopped_repair_whose_dialog_cannot_be_shown_still_does_not_throw()
    {
        var (commands, _, prompt, _) = NewCommands(Stopped);
        prompt.ThrowOnShowError = new InvalidOperationException("the dispatcher has shut down");

        var click = commands.RunAsync(RepairActionKind.UnmountAllAndReprobe);
        var ended = await Record.ExceptionAsync(() => click);

        Assert.Null(ended);
        Assert.Single(prompt.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the button keeps working after a stop: the in-flight flag is cleared, so the next click
    /// (after the wake) reaches the supervisor again.
    /// <b>Catches:</b> the new catch block returning before the flag is reset, leaving "Unmount all" dead until
    /// Bosun restarts.
    /// </summary>
    [Fact]
    public async Task After_a_stopped_repair_the_next_click_runs_the_repair_again()
    {
        var calls = 0;
        var (commands, supervisor, prompt, _) = NewCommands(ct => ++calls == 1 ? Stopped(ct) : Task.CompletedTask);

        await commands.UnmountAllAsync();
        Assert.False(commands.IsUnmountingAll);
        await commands.UnmountAllAsync();

        Assert.Equal(2, supervisor.RepairCalls);
        Assert.Single(prompt.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "A repair that completes before any suspend is pending still returns normally" reaches
    /// the user as no dialog at all.
    /// <b>Catches:</b> a stop dialog shown on every repair (for instance from a check on a flag rather than on the
    /// exception).
    /// </summary>
    [Fact]
    public async Task A_completed_repair_shows_no_dialog()
    {
        var (commands, supervisor, prompt, _) = NewCommands(_ => Task.CompletedTask);

        await commands.UnmountAllAsync();

        Assert.Equal(1, supervisor.RepairCalls);
        Assert.Empty(prompt.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the whole path end to end: a real supervisor stopped partway by a suspend, and the click
    /// shows the stop, not a refusal and not a failure. The drives are off afterwards (I8).
    /// <b>Catches:</b> the supervisor and the command disagreeing on the contract (for example the supervisor
    /// throwing a different type, or wrapping the stop in an <see cref="AggregateException"/>, which the click
    /// would then report as a generic failure).
    /// </summary>
    [Fact]
    public async Task A_real_supervisor_stopped_by_a_suspend_makes_the_click_show_the_stop()
    {
        var drives = new Dictionary<string, string> { ["od1"] = "Q:", ["od2"] = "R:" };
        var h = new PreemptSpecHarness(HostFixtures.Build(
            HostFixtures.Global(suspendUnmountTimeoutSeconds: 3),
            HostFixtures.OnDemand("od1", drive: "Q:"),
            HostFixtures.OnDemand("od2", drive: "R:")));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od1"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od2"));

        var prompt = new RecordingRepairPrompt();
        var commands = new RepairCommands(h.Supervisor, () => null, () => null, prompt, h.Time);
        var unmounts = HoldSet.Unmount(h, drives);

        var click = commands.UnmountAllAsync();
        var pump = h.StartPump();
        var inFlight = await unmounts.InFlightAtArrivalAsync(1, _ => null);
        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts.ReleaseNotArrived(_ => null);
        unmounts[inFlight].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, click);
        await click;

        Assert.Equal(1, prompt.Confirmations);
        var shown = Assert.Single(prompt.Errors);
        Assert.Contains(SleepReason, shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing was unmounted", shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not", shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.False(h.IsMountedInRclone("R:"));
    }

    /// <summary>
    /// <b>Protects:</b> bs-5ga.5, end to end: a suspend that arrives during the repair's LAST drain, with no idle host
    /// and so nothing to skip, still makes the click show the stop (the repair's drained hosts are not re-probed
    /// because a suspend is pending). Same dialog and wording as a repair stopped partway.
    /// <b>Catches:</b> the supervisor returning normally in the last step, so the click shows nothing for a repair
    /// whose re-probe half did not happen.
    /// </summary>
    [Fact]
    public async Task A_real_supervisor_with_a_suspend_during_the_last_drain_makes_the_click_show_the_stop()
    {
        var drives = new Dictionary<string, string> { ["od1"] = "Q:", ["od2"] = "R:" };
        var h = new PreemptSpecHarness(HostFixtures.Build(
            HostFixtures.Global(suspendUnmountTimeoutSeconds: 3),
            HostFixtures.OnDemand("od1", drive: "Q:"),
            HostFixtures.OnDemand("od2", drive: "R:")));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od1"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od2"));

        var prompt = new RecordingRepairPrompt();
        var commands = new RepairCommands(h.Supervisor, () => null, () => null, prompt, h.Time);
        var unmounts = HoldSet.Unmount(h, drives);

        var click = commands.UnmountAllAsync();
        var pump = h.StartPump();
        var last = await unmounts.InFlightAtArrivalAsync(2, _ => null);
        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts[last].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, click);
        await click;

        var shown = Assert.Single(prompt.Errors);
        Assert.Equal(Title, shown.Title);
        Assert.Contains(SleepReason, shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing was unmounted", shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not", shown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.False(h.IsMountedInRclone("R:"));
    }

    private sealed record ShownError(string Title, string Message);

    private sealed class RecordingRepairPrompt : IRepairPrompt
    {
        private readonly List<ShownError> errors = [];

        public IReadOnlyList<ShownError> Errors => errors;

        public int Confirmations { get; private set; }

        public Exception? ThrowOnShowError { get; set; }

        public Task<RepairConfirmation> ConfirmAsync(string title, string message)
        {
            Confirmations++;
            return Task.FromResult(RepairConfirmation.Confirmed);
        }

        public void ShowError(string title, string message)
        {
            errors.Add(new ShownError(title, message));
            if (ThrowOnShowError is { } ex)
            {
                throw ex;
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>An <see cref="IMountSupervisor"/> that answers <see cref="RepairAllAsync"/> as scripted and has no
    /// mounted drives (so no confirmation is asked). Anything else a repair click should not call throws.</summary>
    private sealed class ScriptedRepairSupervisor(Func<CancellationToken, Task> repair) : IMountSupervisor
    {
        public int RepairCalls { get; private set; }

        public Task RepairAllAsync(CancellationToken cancellationToken = default)
        {
            RepairCalls++;
            return repair(cancellationToken);
        }

        public IReadOnlyList<HostMountSnapshot> GetSnapshot() => [];

        public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() => [];

        public Task StartAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        public Task StopAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) => throw Unexpected();

        public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default) => throw Unexpected();

        public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default) => throw Unexpected();

        public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default) => throw Unexpected();

        public Task SuspendAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        public Task ResumeAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        public void NoteUserRequestedRcloneRestart() => throw Unexpected();

        public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) => throw Unexpected();

        public Task ConfigChangedAsync(BosunConfig newConfig, CancellationToken cancellationToken = default) => throw Unexpected();

        private static NotSupportedException Unexpected() => new("not part of a repair click");
    }
}
