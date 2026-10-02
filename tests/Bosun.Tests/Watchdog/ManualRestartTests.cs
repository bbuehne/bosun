using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.UI;
using Bosun.Watchdog;

namespace Bosun.Tests.Watchdog;

/// <summary>
/// bs-aoz: a restart the user asked for is told apart from a watchdog one by its own flag, shows the
/// window, and (see <c>RepairCommandsTests</c>) never touches the watchdog's history. A watchdog
/// restart starts hidden. Fakes only: no process is launched.
/// </summary>
public sealed class ManualRestartTests
{
    private const string Exe = @"C:\Apps\Bosun\Bosun.exe";

    // -- The flag the new instance is launched with ----------------------------------------------

    [Fact]
    public async Task A_manual_restart_launches_the_user_flag_not_the_watchdog_flag()
    {
        var launcher = new FakeLauncher();
        var restarter = NewRestarter(["--other"], launcher);

        var restarted = await restarter.RestartAsync("user asked", RestartKind.Manual, CancellationToken.None);

        Assert.True(restarted);
        Assert.Equal(["--other", "--restarted-by-user", "4242"], Assert.Single(launcher.Launches).Args);
    }

    [Fact]
    public async Task A_watchdog_restart_still_launches_the_watchdog_flag()
    {
        var launcher = new FakeLauncher();
        var restarter = NewRestarter(["--other"], launcher);

        await restarter.RestartAsync("stalled", RestartKind.Watchdog, CancellationToken.None);

        Assert.Equal(["--other", "--restarted-by-watchdog", "4242"], Assert.Single(launcher.Launches).Args);
    }

    [Fact]
    public async Task A_manual_restart_of_an_autostarted_instance_drops_autostart_so_the_window_shows()
    {
        var launcher = new FakeLauncher();
        var restarter = NewRestarter(["--autostart"], launcher);

        await restarter.RestartAsync("user asked", RestartKind.Manual, CancellationToken.None);

        Assert.DoesNotContain("--autostart", Assert.Single(launcher.Launches).Args, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_watchdog_restart_of_an_autostarted_instance_keeps_autostart()
    {
        var launcher = new FakeLauncher();
        var restarter = NewRestarter(["--autostart"], launcher);

        await restarter.RestartAsync("stalled", RestartKind.Watchdog, CancellationToken.None);

        Assert.Contains("--autostart", Assert.Single(launcher.Launches).Args);
    }

    [Fact]
    public void An_earlier_flag_of_either_kind_is_replaced_not_accumulated()
    {
        Assert.Equal(
            ["--other", "--restarted-by-user", "9"],
            RestartHandoffArguments.ForRestart(["--restarted-by-watchdog", "1", "--other"], 9, RestartKind.Manual));
        Assert.Equal(
            ["--other", "--restarted-by-watchdog", "9"],
            RestartHandoffArguments.ForRestart(["--restarted-by-user", "1", "--other"], 9, RestartKind.Watchdog));
        Assert.Equal(
            ["--restarted-by-user", "9"],
            RestartHandoffArguments.ForRestart(["--restarted-by-user=1"], 9, RestartKind.Manual));
    }

    [Theory]
    [InlineData(new[] { "--restarted-by-user", "55" }, RestartKind.Manual, 55)]
    [InlineData(new[] { "--RESTARTED-BY-USER=55" }, RestartKind.Manual, 55)]
    [InlineData(new[] { "--restarted-by-watchdog", "56" }, RestartKind.Watchdog, 56)]
    [InlineData(new[] { "--restarted-by-watchdog", "56", "--restarted-by-user", "55" }, RestartKind.Manual, 55)]
    public void The_handoff_names_the_kind_and_the_process_to_wait_for(string[] args, RestartKind expectedKind, int expectedPid)
    {
        Assert.True(RestartHandoffArguments.TryGetHandoff(args, out var kind, out var pid));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedPid, pid);

        // The new instance waits for the old one whichever flag it was.
        Assert.True(RestartHandoffArguments.TryGetOldProcessId(args, out var waitFor));
        Assert.Equal(expectedPid, waitFor);
    }

    [Theory]
    [InlineData("--restarted-by-user")]
    [InlineData("--restarted-by-user=abc")]
    [InlineData("--restarted-by-user=0")]
    public void A_malformed_user_flag_is_not_a_handoff(string arg)
    {
        Assert.False(RestartHandoffArguments.TryGetHandoff([arg], out _, out var pid));
        Assert.Equal(0, pid);
    }

    // -- What the new instance does with its window ----------------------------------------------

    [Fact]
    public void A_user_restart_is_detected_and_shows_the_window()
    {
        var context = LaunchContextDetector.Detect(["--restarted-by-user", "12"]);

        Assert.Equal(LaunchContext.UserRestart, context);
        Assert.True(context.ShowsWindowAtStartup());
    }

    [Fact]
    public void A_watchdog_restart_is_detected_and_stays_hidden()
    {
        var context = LaunchContextDetector.Detect(["--restarted-by-watchdog", "12"]);

        Assert.Equal(LaunchContext.WatchdogRestart, context);
        Assert.False(context.ShowsWindowAtStartup());
    }

    [Fact]
    public void A_watchdog_restart_of_a_window_that_was_open_is_still_hidden_because_the_flag_not_autostart_decides()
    {
        // No --autostart at all: the user had launched this one by hand. A 3 a.m. recovery still must
        // not pop a window up.
        Assert.Equal(LaunchContext.WatchdogRestart, LaunchContextDetector.Detect(["--restarted-by-watchdog", "12"]));
    }

    [Fact]
    public void A_restart_flag_outranks_autostart_when_both_are_present()
    {
        Assert.Equal(LaunchContext.UserRestart, LaunchContextDetector.Detect(["--autostart", "--restarted-by-user", "12"]));
        Assert.Equal(LaunchContext.WatchdogRestart, LaunchContextDetector.Detect(["--autostart", "--restarted-by-watchdog", "12"]));
    }

    [Fact]
    public void Existing_contexts_are_unchanged()
    {
        Assert.Equal(LaunchContext.Manual, LaunchContextDetector.Detect([]));
        Assert.Equal(LaunchContext.Autostart, LaunchContextDetector.Detect(["--autostart"]));
        Assert.True(LaunchContext.Manual.ShowsWindowAtStartup());
        Assert.False(LaunchContext.Autostart.ShowsWindowAtStartup());
    }

    [Theory]
    [InlineData(LaunchContext.Manual, true)]
    [InlineData(LaunchContext.UserRestart, true)]
    [InlineData(LaunchContext.Autostart, false)]
    [InlineData(LaunchContext.WatchdogRestart, false)]
    public void The_window_controller_shows_the_window_only_for_the_contexts_that_ask_for_it(LaunchContext context, bool shown)
    {
        var window = new Bosun.Tests.UI.Fakes.FakeAppWindow();
        var controller = new MainWindowController(
            window,
            new Bosun.Tests.UI.Fakes.FakeWindowPlacementStore(null),
            new Bosun.Tests.UI.Fakes.FakeVirtualScreenProvider(new ScreenBounds(0, 0, 1920, 1080)));

        controller.Initialize(context);

        Assert.Equal(shown, window.IsVisible);
        Assert.Equal(shown ? 1 : 0, window.ShowCallCount);
    }

    // -- The watchdog records why it restarted ----------------------------------------------------

    [Fact]
    public async Task The_watchdog_restarts_as_a_Watchdog_kind_and_leaves_its_reason_in_the_history()
    {
        var t0 = new DateTimeOffset(2026, 10, 2, 3, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(t0);
        var time = new SleepableTimeProvider(fake);
        var liveness = new FakeLiveness { LastLoopActivityUtc = t0, IsLoopRunning = false };
        var restarter = new FakeRestarter();
        var history = new InMemoryRestartHistory();
        using var health = new Bosun.Health.AppHealthService(time, new Bosun.Health.AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
        using var dog = new SupervisorWatchdog(liveness, health, restarter, history, time, new CapturingLogger<SupervisorWatchdog>());
        await dog.StartAsync(CancellationToken.None);

        time.Advance(new WatchdogOptions().StallThreshold);

        var reason = Assert.Single(restarter.Reasons);
        Assert.Equal([RestartKind.Watchdog], restarter.Kinds);
        Assert.NotNull(history.Last);
        Assert.Equal(reason, history.Last.Reason);
        Assert.Equal(time.GetUtcNow(), history.Last.At);
        Assert.Contains("exited", history.Last.Reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------

    private static ProcessAppRestarter NewRestarter(IReadOnlyList<string> args, FakeLauncher launcher)
    {
        var time = new FakeTimeProvider();
        return new ProcessAppRestarter(
            new RestartContext(Exe, args, ProcessId: 4242),
            launcher,
            new FakeApplicationShutdown(),
            new ShutdownGuard(time, new FakeProcessExiter(), () => null),
            new CapturingLogger<ProcessAppRestarter>());
    }
}
