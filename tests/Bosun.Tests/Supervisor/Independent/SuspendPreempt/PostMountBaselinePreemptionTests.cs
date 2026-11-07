using System.Reflection;
using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// ADR-023 amendment (bs-5ga.3): a suspend that arrives while the post-mount VFS baseline sample is in flight
/// cancels it. The sample is the tail of a mount COMMAND, not a tick, so it is cancelled through the one extra
/// source <c>SuspendAsync</c>'s prologue knows about. A preempted sample is "no baseline taken", never a failed
/// poll: no counter, no fail-closed path, no idle-eligibility change, no error. Only a suspend preempts it.
/// </summary>
/// <remarks>
/// Cancellation is checked through <see cref="Hold.CancellationRequested"/> (token lineage, synchronous), never
/// through the linked token's own flag, which the thread pool sets later and never sets if the supervisor
/// disposes the linked source first (bs-s30, bs-3jd). Time moves only when a test moves it.
/// </remarks>
public sealed class PostMountBaselinePreemptionTests
{
    private const int IdleSeconds = 600;

    private static PreemptSpecHarness NewHarness() =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3),
            HostFixtures.OnDemand("od", idleUnmountSeconds: IdleSeconds, drive: "Q:")));

    /// <summary>Starts a mount of "od" and returns with the sample's first held call reached.</summary>
    private static async Task<(Hold Sample, Task Request, Task Pump)> MountUntilSampleHeldAsync(
        PreemptSpecHarness h, OnCancel onCancel, string operation)
    {
        var sample = h.HRclone.HoldNext(onCancel, operation);
        var request = h.Supervisor.RequestMountAsync("od");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(sample, $"the post-mount baseline sample's {operation}");
        Assert.Equal(1, h.MountCalls("Q:"));
        Assert.Equal(MountState.Mounted, h.State("od"));
        return (sample, request, pump);
    }

    private static bool LoggedFaultWarning(PreemptSpecHarness h) =>
        h.Logger.Entries.Any(
            e => e.Level >= LogLevel.Warning && e.Message.Contains("Could not sample VFS activity", StringComparison.Ordinal));

    /// <summary>
    /// <b>Protects:</b> the amendment's core: the suspend does not wait for the sample, whether it is hung in
    /// <c>vfs/list</c> or in <c>vfs/stats</c>. The fake clock never moves, so the probe timeout (5 s) cannot be
    /// what released it. (A sample that outlasts the 3 s suspend budget is exactly the miss the amendment closes.)
    /// <b>Catches:</b> the sample not being cancellable (the suspend then needs the clock to move);
    /// the sample's source not being published to the prologue; only one of the two calls being linked.
    /// </summary>
    [Theory]
    [InlineData("vfslist")]
    [InlineData("vfsstats")]
    public async Task A_suspend_during_the_baseline_sample_cancels_it_and_does_not_wait_for_the_probe_timeout(string operation)
    {
        var h = NewHarness();
        await h.StartAsync();
        var (sample, request, pump) = await MountUntilSampleHeldAsync(h, OnCancel.LikeProduction, operation);
        var before = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);

        await h.FinishAsync(pump, "the suspend waited for the post-mount VFS baseline sample", request, suspend);
        Assert.Equal(before, h.Now); // nothing but the cancellation released the sample
        Assert.False(sample.CancelCallbackRanInsideSuspendCall, "the cancellation callback ran on the suspend caller's thread");
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> that the sample's cancellation is REQUESTED by the suspend itself, at once, on the
    /// caller's thread: decided by the sample's own source, not left to a thread-pool callback. The held call
    /// ignores its token, so it is still hung when the assertion runs.
    /// <b>Catches:</b> a cancel that is only observed after the call returns; a cancel that is not issued at
    /// all while the call is hung.
    /// </summary>
    [Fact]
    public async Task The_suspend_requests_cancellation_of_the_sample_before_it_returns()
    {
        var h = NewHarness();
        await h.StartAsync();
        var (sample, request, pump) = await MountUntilSampleHeldAsync(h, OnCancel.Ignore, "vfsstats");

        var suspend = SuspendCaller.Call(h.Supervisor);

        Assert.True(sample.CancellationRequested, "the suspend did not cancel the in-flight baseline sample");
        await PreemptSpecHarness.WaitForAsync(() => sample.TokenCancelled, "the cancellation never reached the held call's linked token");
        sample.Release();
        await h.FinishAsync(pump, "the mount command never finished", request, suspend);

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
    }

    /// <summary>
    /// <b>Protects:</b> "a preempted sample is not a poll failure". The held call ignores its cancelled token
    /// and then FAILS, as a call released by a suspend may; the outcome must be discarded because the token
    /// says preempted. Then, after resume and the remount, the idle-unmount is exactly as eligible as ever.
    /// <b>Catches:</b> the failed poll reaching the fail-closed "Which VFS" path (a warning and
    /// <c>VfsPollFaultLogged</c>), the host's idle eligibility being altered, any failure counter moving, an
    /// error line, and the host not being drained as <see cref="DrainCause.Suspend"/>.
    /// </summary>
    [Fact]
    public async Task A_preempted_sample_is_not_a_poll_failure_and_idle_unmount_still_works_after_the_remount()
    {
        var h = NewHarness();
        await h.StartAsync();
        var (sample, request, pump) = await MountUntilSampleHeldAsync(h, OnCancel.Ignore, "vfslist");

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.True(sample.CancellationRequested, "setup: the suspend did not cancel the sample");
        sample.Fail(new HttpRequestException("rcd went away under the suspend"));
        await h.FinishAsync(pump, "the mount command never finished", request, suspend);

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        var snapshot = h.Snapshot("od");
        Assert.Equal(0, snapshot.ConsecutiveMountedFailures);
        Assert.Equal(0, snapshot.ConsecutiveDeepProbeFailures);
        Assert.Equal(0, snapshot.ConsecutiveIdleFailures);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);
        Assert.Empty(h.Errors);
        Assert.False(LoggedFaultWarning(h), "the preempted sample took the fail-closed 'Which VFS' path");
        Assert.Contains(
            h.Logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("baseline sample for od was preempted", StringComparison.Ordinal));

        // ADR-022 brings the drive back; the remount takes its own baseline and idles out normally.
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("od"));
        var remountedAt = h.Now;
        await h.AdvanceSecondsAsync(IdleSeconds + 30);

        Assert.Equal([DrainCause.Suspend, DrainCause.IdleTimeout], h.DrainCauses("od"));
        var idleDrain = h.Transitions("od").Last(t => t.To == MountState.Draining);
        Assert.True(
            idleDrain.TimestampUtc - remountedAt <= TimeSpan.FromSeconds(IdleSeconds + 1),
            "the idle unmount came later than one idle period after the remount");
        Assert.False(LoggedFaultWarning(h), "the remounted host's idle path took the fail-closed 'Which VFS' path");
    }

    /// <summary>
    /// <b>Protects:</b> "any other cancellation or failure behaves as today". Here the sample fails with no
    /// suspend at all: it is a failed poll, the fail-closed warning is logged, no "preempted" line appears, and
    /// the host stays Mounted and undrained.
    /// <b>Catches:</b> preemption being inferred from any failure or cancellation rather than from the suspend's
    /// source (a failed sample would then be swallowed silently, and the idle path's fail-closed warning lost).
    /// </summary>
    [Fact]
    public async Task A_sample_that_fails_without_a_suspend_is_a_failed_poll_as_before()
    {
        var h = NewHarness();
        await h.StartAsync();
        var (sample, request, pump) = await MountUntilSampleHeldAsync(h, OnCancel.LikeProduction, "vfsstats");

        sample.Fail(new HttpRequestException("vfs/stats refused"));
        await h.FinishAsync(pump, "the mount command never finished", request);

        Assert.Equal(MountState.Mounted, h.State("od"));
        Assert.Empty(h.DrainCauses("od"));
        Assert.True(LoggedFaultWarning(h), "a failed sample no longer logs the fail-closed warning");
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("was preempted", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Protects:</b> the same, for the other non-suspend end of a sample: the probe timeout. With no suspend,
    /// the timeout cancels the poll, and that is a failed poll, not a preemption.
    /// <b>Catches:</b> the sample's own source (or its token) being cancelled for reasons other than a suspend, or
    /// the timeout path being read as preemption.
    /// </summary>
    [Fact]
    public async Task A_sample_released_by_the_probe_timeout_is_a_failed_poll_as_before()
    {
        var h = NewHarness();
        await h.StartAsync();
        var (_, request, pump) = await MountUntilSampleHeldAsync(h, OnCancel.LikeProduction, "vfslist");

        h.FireTimers(TimeSpan.FromSeconds(5)); // global.probe_timeout_seconds
        await h.FinishAsync(pump, "the sample was not bounded by the probe timeout", request);

        Assert.Equal(MountState.Mounted, h.State("od"));
        Assert.Empty(h.DrainCauses("od"));
        Assert.True(LoggedFaultWarning(h), "a timed-out sample no longer logs the fail-closed warning");
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("was preempted", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Protects:</b> the race the amendment keeps: a suspend that arrives after the sample has finished has
    /// nothing to cancel, and must not throw or leave anything behind. The sample's source is disposed when it
    /// ends; here the published source is a DISPOSED one (the tiny window between a read and a cancel), and the
    /// suspend's prologue, which must never throw on <c>SystemEvents</c>' thread, still completes.
    /// <b>Catches:</b> a prologue that lets <see cref="ObjectDisposedException"/> escape, or one that lets a
    /// disposed baseline source stop it cancelling the running tick.
    /// </summary>
    [Fact]
    public async Task A_suspend_that_finds_a_disposed_baseline_source_does_not_throw()
    {
        var h = NewHarness();
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        Assert.Equal(MountState.Mounted, h.State("od"));

        var field = typeof(MountSupervisor).GetField("runningBaselineSample", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        field.SetValue(h.Supervisor, disposed);

        Task? suspend = null;
        var thrown = Record.Exception(() => { suspend = SuspendCaller.Call(h.Supervisor); });
        Assert.Null(thrown);
        await h.RunAsync(() => suspend!);

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// <b>Protects:</b> D1's existing skip: a sample is not even started when a suspend is already pending, so
    /// the pending suspend's drain is not delayed by a poll that would only be discarded.
    /// <b>Catches:</b> the skip being lost when the sample became preemptible (the poll would run and be cancelled
    /// afterwards, costing an rc call against a possibly hung rcd).
    /// </summary>
    [Fact]
    public async Task A_suspend_already_pending_still_skips_the_sample_altogether()
    {
        var h = NewHarness();
        await h.StartAsync();
        var mount = h.HRclone.HoldNext(OnCancel.LikeProduction, "mount");
        var request = h.Supervisor.RequestMountAsync("od");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(mount, "mount/mount");

        var suspend = SuspendCaller.Call(h.Supervisor);
        mount.Release();
        await h.FinishAsync(pump, "the mount command never finished", request, suspend);

        Assert.Equal(1, h.MountCalls("Q:"));
        Assert.Equal(0, h.HRclone.Count("vfslist") + h.HRclone.Count("vfsstats") + h.HRclone.Count("vfsqueue"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("baseline sample", StringComparison.Ordinal));
    }
}
