using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// bs-7ck's two whole-host rows: "HOST ADDED: begin supervising it exactly as at startup" and
/// "HOST REMOVED: if mounted, drain first, then stop supervising. Never leave a drive letter with
/// nothing describing it (I2) -- this is the same hazard the host-editor delete path had."
/// </summary>
/// <remarks>
/// <para>
/// The removal row is the one the issue itself flags as previously shipped broken in a different
/// code path, and it is the only row in the whole classification whose failure leaves a drive
/// letter that <b>nothing at all</b> will ever clean up: not the state machine (the host is gone
/// from its records), not reconciliation (which compares against intended state, and the host has
/// no intended state any more), and not the user (who has no tray entry for a host he deleted).
/// </para>
/// <para>
/// The addition row is where the issue's NOTES point: <c>StatusReadModel</c> skips snapshot hosts
/// missing from the live config and, symmetrically, a host in the config that the supervisor has
/// never heard of has no row at all -- so a host added in the window <i>disappears</i> instead of
/// appearing. With twenty Bitvise profiles to import (bs-ww9.9) that is the feature's main path.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock, an in-memory config store.
/// </para>
/// </remarks>
public sealed class ConfigReloadHostSetTests
{
    /// <summary>
    /// <b>Protects:</b> "HOST ADDED: begin supervising it exactly as at startup (Disabled →
    /// Probing for an enabled tier)".
    /// <b>Catches:</b> a diff written only over the intersection of the old and new host sets --
    /// the shape you get from iterating the existing runtime records and looking each one up in the
    /// new config. Every classification then works perfectly and a newly added host is simply never
    /// seen. Adding a host in the window appears to save and then does nothing, forever, which is
    /// the exact experience this issue was raised to remove.
    /// </summary>
    [Fact]
    public async Task A_host_added_by_a_save_starts_being_supervised_immediately()
    {
        var existing = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), existing));

        await harness.StartAsync();
        Assert.False(harness.IsSupervised("archive"));

        var added = HostFixtures.Persistent("archive", probeIntervalSeconds: 60, drive: "R:");
        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), existing, added));

        Assert.True(harness.IsSupervised("archive"), "The added host is not in the supervisor's snapshot.");
        Assert.True(harness.ShallowProbesFor("archive") >= 1, "The added host was never probed.");
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.NotNull(harness.Rclone.FsAt("R:"));

        // The host that was already there is untouched by its neighbour's arrival.
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> the visibility half of the same row -- the added host has a snapshot row
    /// whether or not it is reachable.
    /// <b>Catches:</b> an implementation that only registers a host once it has passed a probe, so
    /// a host added while its machine is off never gets a row. The user's first act after adding a
    /// host is to look for it; a host that is present but unreachable must read as unreachable, not
    /// be absent. Absent is indistinguishable from "the save failed", and the user's next move is
    /// to add it again -- which is how duplicate keys and drive-letter collisions get created.
    /// </summary>
    [Fact]
    public async Task A_host_added_while_its_machine_is_off_still_gets_a_snapshot_row()
    {
        var existing = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), existing));

        await harness.StartAsync();

        var added = HostFixtures.Persistent("archive", probeIntervalSeconds: 60, drive: "R:");
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("archive"), ShallowProbeOutcome.Timeout);

        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), existing, added));

        Assert.True(harness.IsSupervised("archive"), "An unreachable added host has no snapshot row at all.");
        Assert.Equal(MountState.Unreachable, harness.State("archive"));
        Assert.Equal("R:", harness.Snapshot("archive").Drive);
        Assert.Null(harness.Rclone.FsAt("R:")); // I1: no drive letter for a host that did not answer
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I2 on the deletion path -- "if mounted, drain first, then stop
    /// supervising."
    /// <b>Catches:</b> the orphaned drive letter. Dropping the runtime record for a host that has
    /// vanished from the config is the obvious implementation and it is exactly wrong: the drive
    /// stays mounted, and every mechanism that could ever remove it has just been deleted along
    /// with the record. The user deletes a host, sees it disappear from the window, and keeps a
    /// live <c>P:</c> pointing at it -- until the host goes away and Explorer hangs on a drive
    /// letter belonging to a host Bosun no longer believes exists.
    /// </summary>
    [Fact]
    public async Task A_host_removed_while_mounted_has_its_drive_letter_released()
    {
        var kept = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var removed = HostFixtures.Persistent("archive", probeIntervalSeconds: 60, drive: "R:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), kept, removed));

        await harness.StartAsync();
        Assert.NotNull(harness.Rclone.FsAt("R:"));

        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), kept));

        Assert.True(harness.Rclone.UnmountCount("R:") >= 1, "The deleted host's mount was never unmounted.");
        Assert.Null(harness.Rclone.FsAt("R:"));

        // The surviving host is unaffected.
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> the <i>ordering</i> in "drain first, <b>then</b> stop supervising", against
    /// the case docs/ARCHITECTURE.md §4 rule 4 is written for -- an unmount that does not confirm
    /// on the first attempt and has to be retried and escalated.
    /// <b>Catches:</b> an implementation that starts the drain and forgets the host in the same
    /// breath. It passes the test above, because the first <c>mount/unmount</c> is issued and the
    /// in-memory table happens to clear. Here the unmount does not take hold until the third call
    /// -- a wedged WinFsp handle, an open file, rclone busy -- and the retry that rule 4 requires
    /// has to come from a host record that no longer exists. Result: one <c>mount/unmount</c> is
    /// fired, nothing checks whether it worked, and the drive letter survives. That is rule 4's
    /// "never leave the state machine believing a drive is gone when it is not", arrived at by
    /// deleting the state machine.
    /// </summary>
    [Fact]
    public async Task A_removed_hosts_drain_is_retried_until_the_drive_is_really_gone()
    {
        var kept = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var removed = HostFixtures.Persistent("archive", probeIntervalSeconds: 60, drive: "R:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), kept, removed));

        await harness.StartAsync();
        Assert.NotNull(harness.Rclone.FsAt("R:"));

        harness.Rclone.ConfirmUnmountAtCall("R:", 3);

        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), kept));

        for (var i = 0; i < 10 && harness.Rclone.FsAt("R:") is not null; i++)
        {
            await harness.AdvanceSecondsAsync(5);
        }

        Assert.True(
            harness.Rclone.UnmountCount("R:") >= 3,
            $"Only {harness.Rclone.UnmountCount("R:")} unmount attempt(s) were made for a deleted " +
            "host whose unmount did not confirm -- the drain was abandoned with the mount still up.");
        Assert.Null(harness.Rclone.FsAt("R:"));
    }

    /// <summary>
    /// <b>Protects:</b> the other half of the deletion row -- a host that is <i>not</i> mounted
    /// simply stops being supervised.
    /// <b>Catches:</b> a deletion path that unmounts unconditionally "to be safe". That is not
    /// safe: <c>mount/unmount</c> names a mount point, not a host, and the drive letter this host
    /// used to want may be in use by something else entirely -- another Bosun host that has since
    /// been given the letter, or a mount rclone is serving for another tool. A speculative unmount
    /// takes down whatever is actually there. The absence of further probe traffic is ADR-008's
    /// promise: a host nobody has configured generates nothing.
    /// </summary>
    [Fact]
    public async Task Removing_a_host_that_is_not_mounted_issues_no_unmount_and_no_further_probes()
    {
        var kept = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var removed = HostFixtures.OnDemand("archive", probeIntervalSeconds: 60, drive: "R:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), kept, removed));

        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Null(harness.Rclone.FsAt("R:"));

        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), kept));

        Assert.Equal(0, harness.Rclone.UnmountCount("R:"));

        var probes = harness.ShallowProbesFor("archive");
        for (var i = 0; i < 10; i++)
        {
            await harness.AdvanceSecondsAsync(60);
        }

        Assert.Equal(probes, harness.ShallowProbesFor("archive"));
    }
}
