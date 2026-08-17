using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// bs-7ck's first classification row: an edit after which <b>the live mount no longer matches
/// intent</b> -- <c>mount.drive</c>, <c>mount.remote_path</c>, <c>mount.vfs_cache_mode</c>,
/// <c>mount.network_mode</c>, <c>hostname</c>, <c>port</c>, <c>user</c>, <c>identity_file</c>.
/// The DESIGN's instruction for all eight is the same: "DRAIN and let the normal path remount".
/// </summary>
/// <remarks>
/// <para>
/// The half of that sentence which is easy to get wrong is the second half. "Let the normal path
/// remount" is not "remount": the normal path is <c>Draining → Disabled → Probing → Ready →
/// Mounting</c>, and every I1 guarantee lives in the middle of it. A reload handler that drains and
/// then re-mounts directly -- reasonable-looking, because the host was mounted a moment ago and
/// obviously reachable -- skips the probe, and Invariant I1 exists because a drive letter pointing
/// at a host that is not answering blocks Explorer, <c>dir</c>, and every file dialog process-wide
/// across the OS.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock, an in-memory config store.
/// Nothing reaches WinFsp, a real drive letter, a real host, or <c>config/hosts.toml</c>.
/// </para>
/// </remarks>
public sealed class ConfigReloadMountAffectingTests
{
    /// <summary>
    /// <b>Protects:</b> the mount-affecting row of bs-7ck's classification, for every field in it.
    /// <b>Catches:</b> a diff that covers the two obvious fields and forgets the rest. <c>drive</c>
    /// and <c>remote_path</c> are the two the issue text names, so they are the two an
    /// implementation is most likely to cover; <c>user</c>, <c>port</c> and <c>identity_file</c>
    /// look like connection details rather than mount details and are the likeliest to be missed.
    /// Missing any of them leaves a drive letter serving the <i>old</i> definition indefinitely --
    /// the user edits a host, the window says saved, and <c>P:</c> keeps showing the previous
    /// host's files until the next restart. That is worse than the restart-required behaviour it
    /// replaced, because it looks like it worked.
    /// </summary>
    [Theory]
    [InlineData("mount.drive")]
    [InlineData("mount.remote_path")]
    [InlineData("mount.vfs_cache_mode")]
    [InlineData("mount.network_mode")]
    [InlineData("hostname")]
    [InlineData("port")]
    [InlineData("user")]
    [InlineData("identity_file")]
    public async Task A_mount_affecting_edit_tears_down_the_mount_that_no_longer_matches_intent(string field)
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));

        var edited = ConfigReloadFixtures.ApplyMountAffecting(host, field);
        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), edited));

        Assert.True(
            harness.Rclone.UnmountCount("P:") >= 1,
            $"Editing {field} left the old mount at P: in place. bs-7ck classifies it as " +
            "mount-affecting: the live mount no longer matches intent, so it must drain.");
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I1 across a config reload -- the single most dangerous shortcut
    /// available to this feature.
    /// <b>Catches:</b> a reload that drains and remounts without re-probing. The setup is the one
    /// case where that shortcut is provably wrong and entirely plausible: the user retypes the
    /// hostname (a typo, a decommissioned box, a machine that is simply off), and the host that was
    /// answering a second ago is a host that does not exist. A remount without a probe hands the OS
    /// a drive letter backed by nothing, and every Explorer window, file dialog and <c>dir</c> on
    /// the machine hangs -- from a config save, which the user will not connect to the symptom.
    /// The assertion that no mount is ever attempted at <c>P:</c> again is the whole point; the
    /// state assertion alone would pass against an implementation that mounts first and corrects
    /// itself afterwards.
    /// </summary>
    [Fact]
    public async Task Retyping_the_hostname_to_a_host_that_is_not_answering_produces_no_drive_letter()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        const string retyped = "typo.example.internal";
        harness.Probe.SetShallow(retyped, ShallowProbeOutcome.ConnectionRefused);

        await harness.ReloadAsync(
            HostFixtures.Build(HostFixtures.Global(), host with { Hostname = retyped }));

        Assert.True(
            harness.Probe.ShallowCount(retyped) >= 1,
            "The new hostname was never probed, so nothing established whether it is reachable.");
        Assert.Null(harness.Rclone.FsAt("P:"));
        Assert.Equal(1, harness.Rclone.MountCount("P:")); // the original mount and nothing since

        // And it stays that way for as long as the typo does.
        for (var i = 0; i < 10; i++)
        {
            await harness.AdvanceSecondsAsync(300);
            Assert.Null(harness.Rclone.FsAt("P:"));
        }

        Assert.Equal(1, harness.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> docs/ARCHITECTURE.md §4 rules 1 and 2 as an <i>ordering</i> claim on the
    /// reload path: unmount, then a passing shallow probe, then a passing deep probe, and only then
    /// <c>mount/mount</c>.
    /// <b>Catches:</b> the same class of bug as the test above, but on the path where the host is
    /// still perfectly reachable -- which is the overwhelmingly common case and therefore the one
    /// where a missing probe survives every manual test the maintainer will ever run. A reload that
    /// mounts without a deep probe only misbehaves on the day the SSH channel is dead while TCP
    /// still answers, which is precisely the failure mode ADR-016 exists for and the one the v1
    /// acceptance tests (CLAUDE.md §7 items 4 and 5) are built around.
    /// </summary>
    [Fact]
    public async Task A_mount_affecting_edit_re_probes_before_it_remounts()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        var before = harness.Log.Entries.Count;

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { RemotePath = "/srv/elsewhere" } }));

        var since = harness.Log.Entries.Skip(before).ToList();
        var unmount = since.IndexOf("unmount|P:");
        var shallow = since.IndexOf($"shallow|{IndependentHarness.HostnameOf("prod")}|Success");
        var deep = since.IndexOf("deep|prod|Success");
        var remount = since.LastIndexOf("mount|P:");

        Assert.True(unmount >= 0, $"No unmount of P: after the edit.{Environment.NewLine}{harness.Log.Dump()}");
        Assert.True(remount > unmount, $"No remount of P: after the drain.{Environment.NewLine}{harness.Log.Dump()}");
        Assert.True(
            shallow > unmount && shallow < remount,
            $"The remount was not preceded by a successful shallow probe (I1).{Environment.NewLine}{harness.Log.Dump()}");
        Assert.True(
            deep > unmount && deep < remount,
            "The remount was not preceded by a successful deep probe (§4 rule 2 / ADR-016)." +
            $"{Environment.NewLine}{harness.Log.Dump()}");
    }

    /// <summary>
    /// <b>Protects:</b> the <c>mount.drive</c> row end to end -- the old letter is released and the
    /// new one is served, without the user touching anything.
    /// <b>Catches:</b> a reload that adopts the new drive letter in its own bookkeeping but drains
    /// or remounts against the wrong one. Both halves are separately wrong and separately awful:
    /// leaving <c>P:</c> mounted after the user moved the host to <c>Q:</c> orphans a drive letter
    /// nothing in the config describes any more (Invariant I2 -- and once nothing describes it,
    /// nothing will ever clean it up); never mounting <c>Q:</c> means the save silently did
    /// nothing. The snapshot assertion catches the third variant, where both mounts are right but
    /// the UI still shows the old letter.
    /// </summary>
    [Fact]
    public async Task Moving_a_host_to_a_different_drive_letter_releases_the_old_one_and_serves_the_new_one()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.NotNull(harness.Rclone.FsAt("P:"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { Drive = "Q:" } }));

        Assert.Null(harness.Rclone.FsAt("P:"));

        // The remount takes the normal path; give the ladder room in case it is paced.
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(300);

        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.NotNull(harness.Rclone.FsAt("Q:"));
        Assert.Null(harness.Rclone.FsAt("P:"));
        Assert.Equal("Q:", harness.Snapshot("prod").Drive);
    }

    /// <summary>
    /// <b>Protects:</b> the <c>mount.remote_path</c> row -- the remount uses the <i>new</i> fs.
    /// <b>Catches:</b> a reload that correctly decides to drain but then remounts from a
    /// <c>HostConfig</c> it captured at startup. The state machine looks entirely healthy
    /// afterwards, the drive letter is present, and it shows the old directory forever. Nothing in
    /// the state, the snapshot or the transition history can reveal that -- only the mount request
    /// can -- which is why the drain assertion in the theory above is not sufficient on its own.
    /// </summary>
    [Fact]
    public async Task The_remount_after_a_remote_path_edit_uses_the_new_path()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.EndsWith(":/srv/share", harness.Rclone.MountRequests[^1].Fs, StringComparison.Ordinal);

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { RemotePath = "/srv/elsewhere" } }));

        await harness.AdvanceSecondsAsync(300);

        Assert.Equal(2, harness.Rclone.MountCount("P:"));
        Assert.EndsWith(":/srv/elsewhere", harness.Rclone.MountRequests[^1].Fs, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Protects:</b> the <c>mount.vfs_cache_mode</c> row, whose whole justification is Invariant
    /// I6 -- "cache semantics are baked in at mount time", so the only way to change them is to
    /// mount again.
    /// <b>Catches:</b> the same stale-<c>HostConfig</c> bug as the test above, in the one place it
    /// is silent rather than visible. A wrong <c>remote_path</c> is obvious the moment the user
    /// opens the drive; a mount still running <c>writes</c> when the user asked for <c>full</c>
    /// looks completely normal until an application that needs the stronger mode misbehaves, and
    /// nothing connects that back to the edit.
    /// </summary>
    [Fact]
    public async Task The_remount_after_a_vfs_cache_mode_edit_uses_the_new_mode()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal("writes", harness.Rclone.MountRequests[^1].VfsCacheMode);

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { VfsCacheMode = "full" } }));

        await harness.AdvanceSecondsAsync(300);

        Assert.Equal(2, harness.Rclone.MountCount("P:"));
        Assert.Equal("full", harness.Rclone.MountRequests[^1].VfsCacheMode);
    }

    /// <summary>
    /// <b>Protects:</b> bs-7ck's "let the normal path remount" for a host that is <i>not</i>
    /// currently mounted when the edit lands -- the case with no drain to perform.
    /// <b>Catches:</b> a classifier whose mount-affecting branch is written as "drain" and does
    /// nothing else, so a host sitting in <c>Unreachable</c> keeps the old definition and keeps
    /// probing the old hostname forever. The user's fix -- correcting a hostname they got wrong --
    /// then appears to have no effect at all, which is exactly the situation in which they will be
    /// editing.
    /// </summary>
    [Fact]
    public async Task Correcting_the_hostname_of_an_unreachable_host_makes_it_probe_the_corrected_one()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("prod"), ShallowProbeOutcome.DnsFailure);

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.State("prod"));

        const string corrected = "prod-2.example.internal";
        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(), host with { Hostname = corrected }));

        await harness.AdvanceSecondsAsync(300);

        Assert.True(
            harness.Probe.ShallowCount(corrected) >= 1,
            "The corrected hostname was never probed -- the supervisor is still working from the " +
            "definition it read at startup.");
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.NotNull(harness.Rclone.FsAt("P:"));
    }
}
