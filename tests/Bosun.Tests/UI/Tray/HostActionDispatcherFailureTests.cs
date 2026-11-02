using Bosun.Configuration;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.UI.Tray.Fakes;
using Bosun.UI.Tray;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// What a failed Mount or Unmount click does (bs-ngo): a refusal reaches the user through the app's dialog
/// prompt with the supervisor's reason, an unexpected fault is logged and gets a generic message, and
/// shutdown cancelling a request says nothing. The supervisor, the launcher and the prompt are fakes, and
/// the fake requests complete synchronously, so each assertion runs after the whole click has been handled.
/// </summary>
public sealed class HostActionDispatcherFailureTests
{
    private static HostStatusRow Row() => new()
    {
        HostKey = "mercury",
        DisplayName = "Mercury",
        State = MountState.Ready,
        Drive = "P:",
        UserParked = false,
        StatusText = "irrelevant",
        Mode = MountMode.Persistent,
        SessionCount = 0,
        Category = StatusCategory.Pending,
    };

    private sealed record Harness(
        HostActionDispatcher Dispatcher, FakeMountSupervisor Supervisor, FakeRepairPrompt Prompt, CapturingLogger<HostActionDispatcher> Log);

    private static Harness Create(bool withPrompt = true)
    {
        var supervisor = new FakeMountSupervisor();
        var prompt = new FakeRepairPrompt();
        var log = new CapturingLogger<HostActionDispatcher>();
        var dispatcher = new HostActionDispatcher(
            supervisor, new FakeExternalLauncher(), log, prompt: withPrompt ? prompt : null);
        return new Harness(dispatcher, supervisor, prompt, log);
    }

    [Fact]
    public void A_refused_mount_tells_the_user_the_reason_through_the_prompt()
    {
        var h = Create();
        h.Supervisor.MountRequestException = new MountRequestRefusedException("mercury", "the system is suspended");

        h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row());

        var shown = Assert.Single(h.Prompt.Errors);
        Assert.Equal(HostActionFailure.MountTitle, shown.Title);
        Assert.Equal("Mercury was not mounted: the system is suspended.", shown.Message);
    }

    [Fact]
    public void A_mount_refused_because_mounting_is_unavailable_names_that_cause()
    {
        var h = Create();
        h.Supervisor.MountRequestException =
            new MountingUnavailableException("mercury", MountingUnavailableCause.WinFspMissing, "WinFsp is not installed");

        h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row());

        Assert.Equal("Mercury was not mounted: WinFsp is not installed.", Assert.Single(h.Prompt.Errors).Message);
    }

    [Fact]
    public void A_refusal_is_logged_without_a_stack_trace_because_it_is_expected()
    {
        var h = Create();
        h.Supervisor.MountRequestException = new MountRequestRefusedException("mercury", "the system is suspended");

        h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row());

        var entry = Assert.Single(h.Log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("the system is suspended", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_mount_fault_keeps_its_warning_with_the_exception_and_shows_a_generic_message()
    {
        var h = Create();
        var fault = new InvalidOperationException("rc call exploded: http://127.0.0.1:5572/secret");
        h.Supervisor.MountRequestException = fault;

        h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row());

        var entry = Assert.Single(h.Log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(fault, entry.Exception);
        Assert.Contains("Failed to mount mercury from the UI", entry.Message, StringComparison.Ordinal);

        var shown = Assert.Single(h.Prompt.Errors);
        Assert.Equal(
            "Mercury was not mounted, because Bosun hit an unexpected problem. The details are in the Bosun log.", shown.Message);
        Assert.DoesNotContain("exploded", shown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_unmount_is_reported_too()
    {
        var h = Create();
        h.Supervisor.UnmountRequestException = new InvalidOperationException("boom");

        h.Dispatcher.Dispatch(HostMenuActionKind.Unmount, Row());

        var shown = Assert.Single(h.Prompt.Errors);
        Assert.Equal(HostActionFailure.UnmountTitle, shown.Title);
        Assert.StartsWith("Mercury was not unmounted", shown.Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, Assert.Single(h.Log.Entries).Level);
    }

    [Fact]
    public void A_cancelled_request_during_shutdown_is_not_shown_to_the_user()
    {
        var h = Create();
        h.Supervisor.MountRequestException = new OperationCanceledException();

        h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row());

        Assert.Empty(h.Prompt.Errors);
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void A_request_that_succeeds_shows_nothing()
    {
        var h = Create();

        h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row());
        h.Dispatcher.Dispatch(HostMenuActionKind.Unmount, Row());

        Assert.Empty(h.Prompt.Errors);
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public void Without_a_prompt_a_refusal_is_logged_and_the_click_does_not_throw()
    {
        var h = Create(withPrompt: false);
        h.Supervisor.MountRequestException = new MountRequestRefusedException("mercury", "the system is suspended");

        var exception = Record.Exception(() => h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row()));

        Assert.Null(exception);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not shown to the user", StringComparison.Ordinal));
    }

    [Fact]
    public void A_prompt_that_throws_is_logged_and_does_not_escape_the_click()
    {
        var h = Create();
        h.Supervisor.MountRequestException = new MountRequestRefusedException("mercury", "the system is suspended");
        h.Prompt.ShowErrorException = new InvalidOperationException("no window");

        var exception = Record.Exception(() => h.Dispatcher.Dispatch(HostMenuActionKind.Mount, Row()));

        Assert.Null(exception);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Could not show", StringComparison.Ordinal));
    }
}
