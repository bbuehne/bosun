using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.RemountAfterResume;
using static Bosun.Tests.Supervisor.Independent.UnparkAndRepairEnd.UnparkSpec;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-uzy.1 (maintainer decision A, 2026-11-07): the two edge cases bs-uzy left. (1) An edit to
/// persistent while the user's own unmount is still draining leaves the host unparked, because the
/// drain's completion no longer sets the park. (2) Any mode change out of <c>none</c> clears a park.
/// The regression guards pin what must still park.
/// </summary>
/// <remarks>Fakes and a fake clock only (see <see cref="RemountHarness"/>).</remarks>
public sealed class UnparkEdgeCaseTests
{
    private static BosunConfig Persistent() => Config(WithMode(OnDemandHost(), MountMode.Persistent));

    private static BosunConfig OnDemand() => Config(OnDemandHost());

    /// <summary>An on-demand host the user mounted and then unmounted, the unmount held in Draining
    /// until the third unmount call confirms it.</summary>
    private static async Task<RemountHarness> DrainingUserUnmountOfOnDemandAsync()
    {
        var h = new RemountHarness(OnDemand());
        await h.StartAsync();
        await h.MountAsync(Key);
        h.Rclone.ConfirmUnmountAtCall(Drive, 2);
        await h.UnmountAsync(Key);
        Assert.Equal(MountState.Draining, h.State(Key));
        return h;
    }

    // ------------------------------------------------------------------ item 1

    [Fact]
    public async Task An_edit_to_persistent_during_a_user_unmount_drain_that_also_changes_the_mount_ends_unparked_and_mounts_the_new_definition()
    {
        var h = await DrainingUserUnmountOfOnDemandAsync();
        var from = Here(h.Log);
        var persistent = WithMode(OnDemandHost(), MountMode.Persistent);

        // A mount-affecting field changes in the same edit: the new definition is queued as PendingConfig.
        await h.ReloadAsync(Config(persistent with { Port = 2222 }));
        await h.AdvanceSecondsAsync(120);

        var s = h.Snapshot(Key);
        Assert.False(s.UserParked);
        Assert.Equal(MountState.Mounted, s.State);
        Assert.Equal(1, MountCallsAfter(h.Log, Drive, from));
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
    }

    [Fact]
    public async Task An_edit_to_persistent_during_a_user_unmount_drain_does_not_mount_before_the_drain_confirms()
    {
        var h = await DrainingUserUnmountOfOnDemandAsync();
        var from = Here(h.Log);

        await h.ReloadAsync(Persistent());

        Assert.Equal(MountState.Draining, h.State(Key));
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
    }

    [Fact]
    public async Task The_mark_from_an_edit_during_one_user_unmount_drain_does_not_leak_into_the_next_user_unmount()
    {
        var h = await DrainingUserUnmountOfOnDemandAsync();
        await h.ReloadAsync(Persistent());
        await h.AdvanceSecondsAsync(120);
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);

        await h.UnmountAsync(Key);
        await h.AdvanceSecondsAsync(120);

        Assert.True(h.Snapshot(Key).UserParked, "a later, separate user unmount must park as always");
        Assert.NotEqual(MountState.Mounted, h.State(Key));
    }

    // ------------------------------------------------------------------ item 2

    [Fact]
    public async Task A_parked_host_edited_to_none_and_then_to_on_demand_is_unparked()
    {
        var h = await ParkedOnDemandAsync();
        var from = Here(h.Log);

        await h.ReloadAsync(Config(WithMode(OnDemandHost(), MountMode.None)));
        Assert.True(h.Snapshot(Key).UserParked, "a change to none keeps the park");
        await h.ReloadAsync(OnDemand());

        var s = h.Snapshot(Key);
        Assert.False(s.UserParked);
        Assert.Equal(MountState.Ready, s.State);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
    }

    [Fact]
    public async Task An_edit_to_none_keeps_the_park()
    {
        var h = await ParkedOnDemandAsync();

        await h.ReloadAsync(Config(WithMode(OnDemandHost(), MountMode.None)));

        Assert.True(h.Snapshot(Key).UserParked);
        Assert.Equal(MountState.Disabled, h.State(Key));
    }

    // ------------------------------------------------------------------ regression guards

    [Fact]
    public async Task A_user_unmount_drain_that_completes_with_the_host_still_on_demand_parks_it()
    {
        var h = await DrainingUserUnmountOfOnDemandAsync();

        // An unrelated edit during the drain must neither clear nor pre-empt the park.
        var host = OnDemandHost();
        await h.ReloadAsync(Config(host with { Session = host.Session with { TabColor = "#112233" } }));
        await h.AdvanceSecondsAsync(120);

        var s = h.Snapshot(Key);
        Assert.True(s.UserParked);
        Assert.Equal(MountState.Ready, s.State);
        Assert.Null(h.Rclone.FsAt(Drive));
    }

    [Fact]
    public async Task A_user_unmount_of_a_persistent_host_with_no_edit_still_parks_it()
    {
        var h = new RemountHarness(Persistent());
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State(Key));
        h.Rclone.ConfirmUnmountAtCall(Drive, 2);
        await h.UnmountAsync(Key);
        Assert.Equal(MountState.Draining, h.State(Key));
        var from = Here(h.Log);

        await h.AdvanceSecondsAsync(300);

        Assert.True(h.Snapshot(Key).UserParked);
        Assert.NotEqual(MountState.Mounted, h.State(Key));
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
    }

    [Fact]
    public async Task A_persistent_to_on_demand_edit_during_a_user_unmount_drain_still_parks()
    {
        var h = new RemountHarness(Persistent());
        await h.StartAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 2);
        await h.UnmountAsync(Key);
        Assert.Equal(MountState.Draining, h.State(Key));
        var from = Here(h.Log);

        await h.ReloadAsync(OnDemand());
        await h.AdvanceSecondsAsync(120);

        var s = h.Snapshot(Key);
        Assert.True(s.UserParked);
        Assert.Equal(MountState.Ready, s.State);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
    }
}
