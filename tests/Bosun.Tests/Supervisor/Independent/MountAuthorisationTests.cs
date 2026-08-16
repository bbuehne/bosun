using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// Invariant I1 / docs/ARCHITECTURE.md §4 rules 1 and 2, exercised along <b>every</b> route into
/// <c>Mounting</c> rather than only the two obvious ones.
/// </summary>
/// <remarks>
/// The routes are: persistent auto-mount at startup; an on-demand user request; the remount that
/// follows a recovery from <c>Unreachable</c>; the remount after a power resume; the remount after
/// reconciliation notices drift; and the remount after a restarted <c>rclone rcd</c>. Each is a
/// separate code path that could forget the deep probe, and the consequence of forgetting it is
/// identical every time: a drive letter pointing at a host whose SFTP subsystem does not answer,
/// which blocks Explorer process-wide.
/// </remarks>
public sealed class MountAuthorisationTests
{
    /// <summary>
    /// The strongest form of I1 available from outside the class: on one shared, ordered log of
    /// every outbound call, <b>each</b> <c>mount/mount</c> is immediately preceded by a successful
    /// deep probe for the host that owns that drive letter. Bug this catches: a remount path that
    /// reuses a stale deep-probe result, probes the wrong host, or issues the mount first and the
    /// probe afterwards -- none of which any per-collaborator call-count assertion can see.
    /// </summary>
    [Fact]
    public async Task Every_mount_call_is_immediately_preceded_by_a_successful_deep_probe_for_that_host()
    {
        var prod = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        // Drift: rclone loses the mount. Reconciliation must drain and then remount -- and that
        // remount is a fresh entry into Mounting, so it needs its own deep probe.
        harness.Rclone.DropMountSilently("P:");
        await harness.AdvanceSecondsAsync(30);

        Assert.Equal(
            ["deep|prod|Success", "mount|P:", "deep|prod|Success", "mount|P:"],
            harness.Log.Filtered("deep|", "mount|"));
    }

    /// <summary>
    /// Same invariant, two hosts, so a cross-host mix-up (probing A and mounting B) is visible.
    /// Bug this catches: a shared/last-probe-wins deep-probe result across hosts.
    /// </summary>
    [Fact]
    public async Task Every_mount_call_is_authorised_by_a_deep_probe_of_its_own_host_when_several_hosts_mount()
    {
        var prod = HostFixtures.Persistent("prod", drive: "P:");
        var nas = HostFixtures.Persistent("nas", drive: "N:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod, nas));

        await harness.StartAsync();

        AssertEveryMountWasAuthorised(harness.Log, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["P:"] = "prod",
            ["N:"] = "nas",
        });
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(MountState.Mounted, harness.State("nas"));
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §4 rule 5 puts every enabled host back through <c>Probing</c> on resume,
    /// and rule 2 makes the subsequent entry into <c>Mounting</c> conditional on a deep probe. The
    /// resume path is where this matters most operationally: the machine has just woken on a
    /// possibly different network. Bug this catches: a resume path that shortcuts straight back to
    /// the previous <c>Mounted</c> state, or that mounts on the shallow probe alone.
    /// </summary>
    [Fact]
    public async Task A_deep_probe_failure_on_the_post_resume_path_never_produces_a_mount()
    {
        var prod = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.Equal(MountState.Disabled, harness.State("prod"));

        // TCP still answers on the new network, but SFTP does not (expired key, subsystem down).
        // Bounded so that an unbounded-retry bug fails an assertion rather than hanging the run.
        harness.Probe.SetDeep("prod", DeepProbeOutcome.Failed, times: 1);

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        AssertEveryMountWasAuthorised(harness.Log, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["P:"] = "prod",
        });
        Assert.Contains("deep|prod|Failed", harness.Log.Entries);
    }

    /// <summary>
    /// A restarted <c>rclone rcd</c> knows nothing about mounts the supervisor believes in, so
    /// <see cref="IMountSupervisor.OnRcloneRestartedAsync"/> reconciles immediately. The remount
    /// that follows is a new entry into <c>Mounting</c> and needs its own deep probe. Bug this
    /// catches: treating "we were mounted a moment ago" as standing authorisation to remount.
    /// </summary>
    [Fact]
    public async Task The_remount_after_an_rclone_restart_is_deep_probed_again()
    {
        var prod = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod));

        await harness.StartAsync();
        Assert.Equal(1, harness.Probe.DeepCount("prod"));

        harness.Rclone.DropMountSilently("P:"); // fresh rcd: its listmounts is empty
        await harness.RunAsync(() => harness.Supervisor.OnRcloneRestartedAsync());

        Assert.Equal(2, harness.Probe.DeepCount("prod"));
        AssertEveryMountWasAuthorised(harness.Log, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["P:"] = "prod",
        });
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §6 crash recovery. Adoption recognises a mount that already exists; it
    /// is not an entry into <c>Mounting</c> and issues no <c>mount/mount</c>. What must still hold
    /// is that once that adopted mount is drained, getting it back requires the full authorisation
    /// path. Bug this catches: an adoption path that leaves a host flagged as "already authorised"
    /// and lets the next remount skip its deep probe.
    /// </summary>
    [Fact]
    public async Task An_adopted_mount_issues_no_mount_call_and_its_later_remount_is_still_deep_probed()
    {
        var prod = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 1, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, prod));
        harness.Rclone.SeedMount("P:", "bosun-prod:/srv/share");

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(0, harness.Probe.DeepCount("prod"));
        Assert.Equal(0, harness.Rclone.TotalMountCalls);

        // One failed mounted probe (threshold 1) drains it; the shallow probe then succeeds again
        // and the persistent tier remounts -- which must be deep-probed.
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("prod"), ShallowProbeOutcome.Timeout, times: 1);
        await harness.AdvanceSecondsAsync(60);

        Assert.Equal(1, harness.Rclone.UnmountCount("P:"));
        AssertEveryMountWasAuthorised(harness.Log, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["P:"] = "prod",
        });
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §4 rule 1: there is no path from <c>Unreachable</c> or <c>Probing</c>
    /// to <c>Mounting</c>, and a user pressing "mount" cannot create one. Bug this catches: a tray
    /// command that treats an explicit user request as sufficient authority -- exactly the shape of
    /// change that looks reasonable in review and hangs Explorer in production.
    /// </summary>
    [Fact]
    public async Task A_user_mount_request_for_an_unreachable_host_issues_no_deep_probe_and_no_mount()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.State("archive"));

        // ADR-014 rule 8 (ratified AFTER this test was written) changed the calling convention only:
        // the request now issues an immediate SHALLOW probe and, when that fails, throws with a
        // causal reason instead of returning quietly. The three assertions below -- the actual
        // protective content of this test -- are unchanged and still hold, because a failed shallow
        // probe leaves the host Unreachable and rule 1 still forbids Mounting from there.
        await Assert.ThrowsAsync<MountRequestRefusedException>(
            () => harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive")));

        Assert.Equal(MountState.Unreachable, harness.State("archive"));
        Assert.Equal(0, harness.Probe.DeepCount("archive"));
        Assert.Equal(0, harness.Rclone.TotalMountCalls);
    }

    /// <summary>
    /// Bug this catches: a second <c>mount/mount</c> for a drive letter that is already mounted --
    /// a double-click on the tray item, or a duplicate command, producing two rclone mounts
    /// fighting over one drive letter.
    /// </summary>
    [Fact]
    public async Task A_mount_request_for_an_already_mounted_host_is_ignored()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
        Assert.Equal(1, harness.Probe.DeepCount("archive"));
    }

    /// <summary>
    /// Bug this catches: a mount request accepted while a drain is still in flight, so a new mount
    /// lands on a drive letter whose previous unmount has not been confirmed gone -- the precise
    /// situation docs/ARCHITECTURE.md §4 rule 4 exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_mount_request_while_the_host_is_still_draining_is_ignored()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        harness.Rclone.ConfirmUnmountAtCall("Q:", int.MaxValue); // the unmount never takes effect
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        Assert.Equal(MountState.Draining, harness.State("archive"));

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(MountState.Draining, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// Bug this catches: a tray "mount" click that is honoured while the machine is on its way into
    /// suspend, bringing a drive letter back up moments before the box sleeps -- Invariant I8 in
    /// reverse.
    /// </summary>
    [Fact]
    public async Task A_mount_request_while_suspended_is_ignored()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.Equal(MountState.Disabled, harness.State("archive"));

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(MountState.Disabled, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// Every probe must carry the configured timeout. Bug this catches: an untimed probe. The
    /// supervisor loop is single-threaded (docs/ARCHITECTURE.md §5), so one probe that never
    /// returns stops every other host's state machine -- including the failure accounting that
    /// would have unmounted a dead drive.
    /// </summary>
    [Fact]
    public async Task Probes_are_issued_with_the_configured_probe_timeout()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(probeTimeoutSeconds: 7);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(7), harness.Probe.LastShallowTimeout);
        Assert.Equal(TimeSpan.FromSeconds(7), harness.Probe.LastDeepTimeout);
    }

    /// <summary>
    /// ADR-005 ("do not retry through failure") and docs/ARCHITECTURE.md §6's "drive letter already
    /// in use: refuse the mount, surface the conflict, do not silently pick another letter".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A persistent host whose deep probe keeps failing -- TCP up, SFTP down, which is what an
    /// expired key or a disabled subsystem looks like -- travels
    /// <c>Ready -&gt; Mounting -&gt; Draining -&gt; Disabled -&gt; Probing -&gt; Ready</c> and
    /// arrives back at the same auto-mount decision. Nothing in that cycle waits for the clock, so
    /// the spec as written permits it to spin. This test bounds it: at most a couple of attempts
    /// before the supervisor must wait.
    /// </para>
    /// <para>
    /// The deep probe is scripted to fail a bounded number of times and then succeed purely so a
    /// failure here is a readable assertion about a call count rather than a hung test host.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_persistent_host_whose_deep_probe_keeps_failing_does_not_retry_without_bound()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDeep("prod", DeepProbeOutcome.Failed, times: 25);

        await harness.StartAsync();

        Assert.True(
            harness.Probe.DeepCount("prod") <= 2,
            $"deep probe was retried {harness.Probe.DeepCount("prod")} times inside a single StartAsync with no " +
            "clock advance -- the failed-mount cycle is not rate-limited. Log:" + Environment.NewLine + harness.Log.Dump());
    }

    /// <summary>Same unbounded-retry cycle, reached through a failing <c>mount/mount</c> instead of
    /// a failing deep probe -- the "drive letter already in use" case named in
    /// docs/ARCHITECTURE.md §6.</summary>
    [Fact]
    public async Task A_persistent_host_whose_mount_call_keeps_failing_does_not_retry_without_bound()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.FailMount("P:", new InvalidOperationException("drive letter already in use"), times: 25);

        await harness.StartAsync();

        Assert.True(
            harness.Rclone.MountCount("P:") <= 2,
            $"mount/mount was retried {harness.Rclone.MountCount("P:")} times inside a single StartAsync with no " +
            "clock advance. Log:" + Environment.NewLine + harness.Log.Dump());
    }

    /// <summary>
    /// Asserts, over the shared ordered call log, that every <c>mount/mount</c> was immediately
    /// preceded by a successful deep probe of the host that owns that mount point.
    /// </summary>
    private static void AssertEveryMountWasAuthorised(
        SupervisorCallLog log, IReadOnlyDictionary<string, string> hostKeyByDrive)
    {
        var relevant = log.Filtered("deep|", "mount|");

        for (var i = 0; i < relevant.Count; i++)
        {
            var parts = relevant[i].Split('|');
            if (!string.Equals(parts[0], "mount", StringComparison.Ordinal))
            {
                continue;
            }

            var drive = parts[1];
            Assert.True(
                hostKeyByDrive.TryGetValue(drive, out var hostKey),
                $"mount at {drive} belongs to no host this test knows about");

            Assert.True(i > 0, $"mount at {drive} was the first call of all -- nothing authorised it");
            Assert.Equal($"deep|{hostKey}|Success", relevant[i - 1]);
        }
    }
}
