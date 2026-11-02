using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Bosun.UI.Tray;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-z9e: a user-requested "Restart rclone" drops every mount by design, so the reconciliation that
/// follows must drain them silently (<see cref="DrainCause.Repair"/>), while an rcd crash restart must
/// still toast (<see cref="DrainCause.Automatic"/>), and the user's mark must not leak into a later
/// crash. "Toasts" below is <see cref="UnexpectedUnmountDetector.IsUnexpectedUnmount"/> applied to the
/// real supervisor's real history, not a restatement of the cause.
/// </summary>
public sealed class UserRcloneRestartTests
{
    private static SupervisorHarness Harness() =>
        new(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));

    private static List<MountTransitionEntry> DrainStarts(SupervisorHarness harness) =>
        harness.Supervisor.GetTransitionHistory()
            .Where(e => e.HostKey == "prod" && e.To == MountState.Draining)
            .Reverse()
            .ToList();

    private static bool Toasts(MountTransitionEntry entry) => UnexpectedUnmountDetector.IsUnexpectedUnmount(entry);

    [Fact]
    public async Task A_user_restart_drops_the_mount_silently()
    {
        var harness = Harness();
        await harness.StartAsync();
        harness.Supervisor.NoteUserRequestedRcloneRestart();
        harness.Rclone.SimulateMountLost("P:"); // what a fresh rcd's listmounts looks like

        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        var entry = Assert.Single(DrainStarts(harness));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.Repair, entry.DrainCause);
        Assert.False(Toasts(entry));
    }

    [Fact]
    public async Task A_crash_restart_still_toasts()
    {
        var harness = Harness();
        await harness.StartAsync();
        harness.Rclone.SimulateMountLost("P:");

        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        var entry = Assert.Single(DrainStarts(harness));
        Assert.Equal(DrainCause.Automatic, entry.DrainCause);
        Assert.True(Toasts(entry));
    }

    [Fact]
    public async Task A_crash_after_a_user_restarts_reconciliation_toasts()
    {
        var harness = Harness();
        await harness.StartAsync();
        harness.Supervisor.NoteUserRequestedRcloneRestart();
        harness.Rclone.SimulateMountLost("P:");
        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        // The persistent host re-probes and mounts again; then rcd crashes within the mark's lifetime.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        harness.Rclone.SimulateMountLost("P:");
        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        var drains = DrainStarts(harness);
        Assert.Equal(2, drains.Count);
        Assert.Equal(DrainCause.Repair, drains[0].DrainCause);
        Assert.Equal(DrainCause.Automatic, drains[1].DrainCause);
        Assert.False(Toasts(drains[0]));
        Assert.True(Toasts(drains[1]));
    }

    [Fact]
    public async Task The_mark_expires_when_no_reconciliation_consumes_it()
    {
        var harness = Harness();
        await harness.StartAsync();
        harness.Supervisor.NoteUserRequestedRcloneRestart(); // the restart then never became healthy

        await harness.AdvanceAsync(MountSupervisor.UserRestartMarkLifetime + TimeSpan.FromSeconds(1));
        Assert.Empty(DrainStarts(harness));
        harness.Rclone.SimulateMountLost("P:"); // a later, unrelated crash
        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        var entry = Assert.Single(DrainStarts(harness));
        Assert.Equal(DrainCause.Automatic, entry.DrainCause);
        Assert.True(Toasts(entry));
    }

    [Fact]
    public async Task The_mark_is_still_held_inside_its_lifetime()
    {
        var harness = Harness();
        await harness.StartAsync();
        harness.Supervisor.NoteUserRequestedRcloneRestart();

        await harness.AdvanceAsync(MountSupervisor.UserRestartMarkLifetime - TimeSpan.FromSeconds(1));
        harness.Rclone.SimulateMountLost("P:");
        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        Assert.Equal(DrainCause.Repair, Assert.Single(DrainStarts(harness)).DrainCause);
    }

    [Fact]
    public async Task A_reconciliation_tick_that_beats_the_healthy_report_is_silent_too()
    {
        var harness = Harness();
        await harness.StartAsync();
        harness.Supervisor.NoteUserRequestedRcloneRestart();
        harness.Rclone.SimulateMountLost("P:");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // ReconciliationInterval, before OnRcloneRestartedAsync

        var entry = Assert.Single(DrainStarts(harness));
        Assert.Equal(DrainCause.Repair, entry.DrainCause);
        Assert.False(Toasts(entry));
    }
}
