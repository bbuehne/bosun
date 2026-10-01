using Bosun.Diagnostics;
using Bosun.Tests.Diagnostics.Fakes;
using Bosun.Tests.UI.Tray.Fakes;
using Bosun.UI.Tray;

namespace Bosun.Tests.Diagnostics;

public sealed class CopyDiagnosticsCommandTests
{
    private readonly FakeBundleBuilder _builder = new();
    private readonly FakeExplorerRevealer _revealer = new();
    private readonly FakeErrorPresenter _presenter = new();

    private CopyDiagnosticsCommand Create() => new(_builder, _revealer, _presenter);

    [Fact]
    public async Task A_finished_bundle_is_revealed_in_explorer_and_no_error_is_shown()
    {
        await Create().RunAsync();

        Assert.Equal([_builder.Path], _revealer.Revealed);
        Assert.Empty(_presenter.Errors);
    }

    [Fact]
    public async Task A_failed_build_shows_the_error_to_the_user_and_reveals_nothing()
    {
        _builder.Throws = new IOException("The disk is full");

        await Create().RunAsync();

        Assert.Empty(_revealer.Revealed);
        var shown = Assert.Single(_presenter.Errors);
        Assert.Contains("The disk is full", shown);
    }

    [Fact]
    public async Task A_failure_to_open_explorer_is_not_reported_as_a_failed_bundle()
    {
        _revealer.Throws = new InvalidOperationException("no shell");

        await Create().RunAsync();

        Assert.Empty(_presenter.Errors);
    }

    [Fact]
    public async Task A_presenter_that_throws_does_not_escape_the_command()
    {
        _builder.Throws = new IOException("boom");
        _presenter.Throws = new InvalidOperationException("no dispatcher");

        await Create().RunAsync();

        Assert.Single(_presenter.Errors);
    }

    [Fact]
    public async Task A_second_click_while_a_build_is_running_is_ignored_and_the_command_is_reusable_afterwards()
    {
        _builder.Gate = new TaskCompletionSource();
        var command = Create();

        var first = command.RunAsync();
        Assert.True(command.IsRunning);
        await command.RunAsync();
        Assert.Equal(1, _builder.Calls);

        _builder.Gate.SetResult();
        await first;
        Assert.False(command.IsRunning);

        _builder.Gate = null;
        await command.RunAsync();
        Assert.Equal(2, _builder.Calls);
    }

    [Fact]
    public async Task The_shared_dispatcher_path_runs_the_command_for_both_the_tray_and_the_window()
    {
        var command = Create();
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), new FakeExternalLauncher(), diagnostics: command);

        dispatcher.CopyDiagnostics();

        // The dispatcher fires and forgets; the fake builder completes synchronously, so the whole
        // command has already run by the time CopyDiagnostics returns.
        Assert.Equal(1, _builder.Calls);
        Assert.Equal([_builder.Path], _revealer.Revealed);
    }

    [Fact]
    public void A_dispatcher_with_no_diagnostics_command_ignores_the_request_instead_of_throwing()
    {
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), new FakeExternalLauncher());

        dispatcher.CopyDiagnostics();
    }

    [Fact]
    public void Copy_diagnostics_never_touches_the_supervisor()
    {
        var supervisor = new FakeMountSupervisor();
        var dispatcher = new HostActionDispatcher(supervisor, new FakeExternalLauncher(), diagnostics: Create());

        dispatcher.CopyDiagnostics();

        Assert.Empty(supervisor.MountRequests);
        Assert.Empty(supervisor.UnmountRequests);
        Assert.Empty(supervisor.RetryNowRequests);
    }
}
