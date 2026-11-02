using Bosun.Probe;
using Bosun.Supervisor;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using static Bosun.Tests.Supervisor.Independent.RemountAfterResume.RemountSpec;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>
/// ADR-022's second amendment (2026-11-01, bs-ane). A remount record that is WAITING (kept because
/// mounting is unavailable, or because <c>rclone rcd</c> failed ADR-017's <c>core/version</c> gate)
/// expires ten minutes after the LATEST resume: it is dropped with an Info log line, the host is not
/// remounted automatically, and a user click still mounts it as usual.
/// Fakes and a fake clock only (see <see cref="RemountHarness"/>); the ten minutes below are written
/// out rather than read from the production constant, so a change to it must change these tests too.
/// </summary>
public sealed class RemountExpiryTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    private static readonly MountingAvailability RcdRestarting =
        MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rcd restarting");

    private static Task MakeAvailableAsync(RemountHarness h) =>
        h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));

    private static Task MakeUnavailableAsync(RemountHarness h) =>
        h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));

    /// <summary>User-mounted, suspended, mounting unavailable, resumed: waiting on availability.</summary>
    private static async Task<(RemountHarness H, int ResumeMark)> WaitingForAvailabilityAsync()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await MakeUnavailableAsync(h);
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        return (h, resumeMark);
    }

    /// <summary>User-mounted, suspended, rcd failing <c>core/version</c>, resumed: waiting on rcd.</summary>
    private static async Task<(RemountHarness H, int ResumeMark)> WaitingForRcdAsync()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Rc.VersionFails = true;
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        return (h, resumeMark);
    }

    private static bool Dropped(RemountHarness h) => h.Logger.Entries.Any(e =>
        e.Level == LogLevel.Information
        && e.Message.Contains("Dropping the remount record", StringComparison.Ordinal)
        && e.Message.Contains("second amendment", StringComparison.Ordinal));

    // ------------------------------------------------------------------------------------------
    // The limit, from the availability gate
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the near side of the limit: mounting returns at 9:59 after the resume, and the
    /// waiting host is still re-probed and mounted, deep probe first (I1), exactly once.
    /// <b>Catches:</b> an expiry that fires early (a limit read as minutes-to-seconds, a clock started
    /// at the wrong moment).
    /// </summary>
    [Fact]
    public async Task Availability_returning_just_inside_the_limit_still_mounts_the_host()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();

        await h.AdvanceAsync(Limit - TimeSpan.FromSeconds(1));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.False(Dropped(h));
        await MakeAvailableAsync(h);

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
        Assert.False(Dropped(h));
    }

    /// <summary>
    /// <b>Protects:</b> the far side: mounting returns at 10:01 and nothing mounts. The record is
    /// gone, which is shown three ways: the host rests in <c>Ready</c> and is never re-probed for it
    /// (no shallow probe from the availability change), an Info line says it was dropped, and later
    /// availability flaps and a network change still do not mount it.
    /// <b>Catches:</b> no expiry at all; an expiry that only blocks the next sweep and lets a later
    /// one mount; a drop that is silent.
    /// </summary>
    [Fact]
    public async Task Availability_returning_just_past_the_limit_does_not_mount_and_the_record_is_gone()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();

        await h.AdvanceAsync(Limit + TimeSpan.FromSeconds(1));
        var shallowBefore = h.Probe.ShallowCount(OnDemandHostname);
        var deepBefore = h.Probe.DeepCount(OnDemandKey);
        var transitionsBefore = h.Transitions(OnDemandKey).Count;
        await MakeAvailableAsync(h);

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        Assert.Equal(shallowBefore, h.Probe.ShallowCount(OnDemandHostname));
        Assert.Equal(deepBefore, h.Probe.DeepCount(OnDemandKey));
        Assert.Empty(h.Transitions(OnDemandKey).Skip(transitionsBefore));
        Assert.True(Dropped(h), "expected an Info line saying the record was dropped");

        await MakeUnavailableAsync(h);
        await MakeAvailableAsync(h);
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.RunAsync(() => h.Supervisor.OnRcloneRestartedAsync());
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> the expiry does not depend on anything other than the 30 s reconciliation
    /// tick noticing it: nothing else touches the supervisor for eleven minutes, then the clock has
    /// moved past the limit, and the record is dropped (Info line) with no availability event, probe
    /// or click to prompt it. This is the "does not linger in memory" case.
    /// <b>Catches:</b> an expiry checked only inside the sweep, which would leave the record in memory
    /// until some unrelated event.
    /// </summary>
    [Fact]
    public async Task The_reconciliation_tick_drops_an_expired_record_without_any_other_event()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();
        var shallowBefore = h.Probe.ShallowCount(OnDemandHostname);

        await h.AdvanceAsync(Limit - TimeSpan.FromSeconds(1));
        Assert.False(Dropped(h));
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        Assert.True(Dropped(h), "expected the tick to drop the record");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Equal(shallowBefore, h.Probe.ShallowCount(OnDemandHostname));
    }

    // ------------------------------------------------------------------------------------------
    // The limit, from the rcd gate
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the rcd route's near side: rcd answers again before the limit, the tick sweeps,
    /// and the host mounts.
    /// <b>Catches:</b> an expiry that disturbs the existing tick route.
    /// </summary>
    [Fact]
    public async Task Rcd_answering_again_inside_the_limit_mounts_the_host()
    {
        var (h, resumeMark) = await WaitingForRcdAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(9));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        h.Rc.VersionFails = false;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> the rcd route's far side: rcd stays silent past ten minutes, then answers. The
    /// host does not mount. The tick also stops spending a <c>core/version</c> call on the record once
    /// it has expired (the version-call count is flat after the expiry, with rcd still failing).
    /// <b>Catches:</b> a reconciliation-tick route that bypasses the expiry; a tick that keeps polling
    /// rcd on behalf of a record that no longer exists.
    /// </summary>
    [Fact]
    public async Task Rcd_answering_again_after_the_limit_does_not_mount_and_polling_stops()
    {
        var (h, resumeMark) = await WaitingForRcdAsync();

        await h.AdvanceAsync(Limit + TimeSpan.FromSeconds(31));
        Assert.True(Dropped(h), "expected an Info line saying the record was dropped");
        var versionCallsAtExpiry = h.Rc.VersionCalls;
        Assert.True(versionCallsAtExpiry > 0, "setup: the tick should have been polling rcd while the record waited");

        await h.AdvanceAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(versionCallsAtExpiry, h.Rc.VersionCalls);

        h.Rc.VersionFails = false;
        await h.AdvanceAsync(TimeSpan.FromMinutes(3));
        await h.RunAsync(() => h.Supervisor.OnRcloneRestartedAsync());
        await h.AdvanceAsync(TimeSpan.FromMinutes(3));

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
    }

    // ------------------------------------------------------------------------------------------
    // A second sleep
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the choice that a second suspend/resume RESTARTS the ten minutes (each wake is
    /// a new "waking up"). Eight minutes after the first resume the machine sleeps again for 30
    /// minutes (time passes while suspended, and nothing may expire then), resumes, and mounting
    /// returns eight minutes after THAT resume: 46 minutes after the first, but inside the limit
    /// counted from the latest wake, so the host mounts.
    /// <b>Catches:</b> a deadline fixed at the first resume (the record would be gone); a deadline
    /// that runs through the sleep; a second resume that does not reset it.
    /// </summary>
    [Fact]
    public async Task A_second_sleep_restarts_the_ten_minutes_from_the_latest_resume()
    {
        var (h, firstResumeMark) = await WaitingForAvailabilityAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(8));

        await h.SuspendAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.False(Dropped(h), "nothing expires while the machine is asleep");
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        await h.AdvanceAsync(TimeSpan.FromMinutes(8));
        Assert.False(Dropped(h));
        await MakeAvailableAsync(h);

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, firstResumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, firstResumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> the other half of the same choice: the restarted clock still expires. Ten
    /// minutes and a second after the SECOND resume mounting returns and the host does not mount.
    /// <b>Catches:</b> a second resume that clears the deadline instead of resetting it, leaving a
    /// record that never expires.
    /// </summary>
    [Fact]
    public async Task After_a_second_sleep_the_record_still_expires_ten_minutes_after_the_latest_resume()
    {
        var (h, firstResumeMark) = await WaitingForAvailabilityAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(8));
        await h.SuspendAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(30));
        await h.ResumeAsync();

        await h.AdvanceAsync(Limit + TimeSpan.FromSeconds(1));
        await MakeAvailableAsync(h);

        Assert.True(Dropped(h));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, firstResumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> a record that has already outlived the limit when the machine sleeps again is
    /// not resurrected by the next wake: the suspend drops it, and the second resume does not mount it
    /// even though everything is then available.
    /// <b>Catches:</b> a resume that re-arms a deadline for a record that should already be gone.
    /// </summary>
    [Fact]
    public async Task A_record_already_expired_when_the_machine_sleeps_again_is_not_revived_by_the_wake()
    {
        var (h, firstResumeMark) = await WaitingForAvailabilityAsync();
        // Either the reconciliation tick or the suspend drops it; which one does not matter here. What
        // matters is that the wake finds nothing to mount.
        await h.AdvanceAsync(Limit + TimeSpan.FromSeconds(5));

        await h.SuspendAsync();
        await MakeAvailableAsync(h);
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.True(Dropped(h));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, firstResumeMark));
    }

    // ------------------------------------------------------------------------------------------
    // After expiry the host is an ordinary on-demand host
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "a user click still works as usual" after the record has expired: the click
    /// mounts the host once, through the ordinary path (shallow-gated Ready, deep probe first, I1), and
    /// the idle-unmount and unmount behaviour that follow are an ordinary host's.
    /// <b>Catches:</b> an expiry that leaves the host in a state a click cannot mount from (parked,
    /// disabled, unreachable); a drop that damages the host rather than the record.
    /// </summary>
    [Fact]
    public async Task A_user_click_after_the_record_expired_mounts_the_host_normally()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();
        await h.AdvanceAsync(Limit + TimeSpan.FromSeconds(31));
        await MakeAvailableAsync(h);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        await h.MountAsync(OnDemandKey);

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);

        await h.UnmountAsync(OnDemandKey);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        await h.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
    }
}
