using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Independent.SuspendBaselineAndRepairStop;

/// <summary>
/// Shared setup for the bs-5ga.3 tests in this folder, written from ADR-023's amendment of 2026-11-07
/// ("a suspend preempts the post-mount VFS baseline sample") and docs/ARCHITECTURE.md §4 rules 5 and 7,
/// not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The timing that makes a missing baseline visible.</b> Rule 7, "Safety when the clock fires": a clock
/// that fires with no earlier sample to compare against "takes one and waits one more period". With the
/// idle period (30 s) shorter than the mounted-probe interval (60 s), no mounted probe samples before the
/// clock first fires, so:
/// </para>
/// <list type="bullet">
/// <item>baseline taken at the mount: an idle drive unmounts at mount + 30 s;</item>
/// <item>no baseline taken at the mount: the clock at + 30 s takes one, and the drive unmounts at + 60 s.</item>
/// </list>
/// <para>
/// Fakes and a fake clock only (<see cref="PreemptSpecHarness"/>): no real rclone, drive letter, host,
/// WinFsp, fragment path or wt.exe.
/// </para>
/// </remarks>
internal static class BaselineSpec
{
    public const int IdleSeconds = 30;

    public const int MountedProbeIntervalSeconds = 60;

    /// <summary>The fs Bosun passes to <c>mount/mount</c> (rule 7, "Which VFS": <c>bosun-&lt;key&gt;:&lt;remote_path&gt;</c>;
    /// <c>HostFixtures</c> uses the remote path <c>/srv/share</c>).</summary>
    public static string FsOf(string key) => $"bosun-{key}:/srv/share";

    public static HostConfig OnDemand(string key, string drive, int idleSeconds = IdleSeconds) =>
        HostFixtures.OnDemand(key, probeIntervalSeconds: 0, idleUnmountSeconds: idleSeconds, drive: drive);

    public static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(
                failuresBeforeUnmount: 3,
                mountedProbeIntervalSeconds: MountedProbeIntervalSeconds,
                suspendUnmountTimeoutSeconds: 3),
            hosts));

    /// <summary>Every <c>vfs/list</c> and <c>vfs/stats</c> call so far.</summary>
    public static int SampleCalls(PreemptSpecHarness h) => h.HRclone.Count("vfslist") + h.HRclone.Count("vfsstats");

    /// <summary>Every host's <c>X -&gt; Mounting</c> entry so far.</summary>
    public static int MountingEntries(PreemptSpecHarness h) =>
        h.Supervisor.GetTransitionHistory().Count(t => t.To == MountState.Mounting);

    /// <summary>Log lines at Warning or above logged after the first <paramref name="skip"/> entries.</summary>
    public static IReadOnlyList<TimedLogEntry> WarningsSince(PreemptSpecHarness h, int skip) =>
        h.Logger.Entries.Skip(skip).Where(e => e.Level >= LogLevel.Warning).ToList();

    public static string Describe(IEnumerable<TimedLogEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(e => $"  [{e.Level}] {e.Message}"));

    /// <summary>Fake-clock time of <paramref name="hostKey"/>'s transitions into <c>Draining</c> with
    /// <paramref name="cause"/>, oldest first.</summary>
    public static IReadOnlyList<DateTimeOffset> DrainTimes(PreemptSpecHarness h, string hostKey, DrainCause cause) =>
        h.Transitions(hostKey).Where(t => t.To == MountState.Draining && t.DrainCause == cause).Select(t => t.TimestampUtc).ToList();

    /// <summary>Fake-clock time of the latest <c>Mounting -&gt; Mounted</c> of <paramref name="hostKey"/>.</summary>
    public static DateTimeOffset LastMountedAt(PreemptSpecHarness h, string hostKey) =>
        h.Transitions(hostKey).Last(t => t.To == MountState.Mounted).TimestampUtc;

    public static string Dump(PreemptSpecHarness h, string hostKey) =>
        string.Join(Environment.NewLine, h.Transitions(hostKey).Select(t =>
            $"  {t.TimestampUtc:HH:mm:ss.fff} {t.From} -> {t.To} ({t.Trigger}; cause {t.DrainCause?.ToString() ?? "-"})"));

    /// <summary>
    /// A POSITIVE check that a suspend cancelled <paramref name="hold"/>'s call, made once, right after
    /// <c>SuspendAsync</c> returned: the suspend must have REQUESTED the cancellation by then (ADR-023 decision 2,
    /// <c>CancelAsync</c> on the caller's thread), whatever the thread pool has run since. A single read is sound
    /// since bs-7z8; before it, <see cref="TokenLineage"/> could answer "not requested" here when the held call
    /// unwound and disposed its <c>vfs/*</c> linked source in the middle of the walk.
    /// </summary>
    public static void AssertSuspendCancelled(Hold hold, string what) =>
        Assert.True(hold.CancellationRequested, $"SuspendAsync did not cancel {what} (bs-5ga.3)");

    /// <summary>
    /// Mounts <paramref name="hostKey"/> on demand with its baseline sample's <paramref name="operation"/>
    /// call ("vfslist" or "vfsstats") held, calls <c>SuspendAsync</c> while it is in flight, and finishes.
    /// Returns the mount request (completed, outcome not yet inspected) and the hold.
    /// </summary>
    public static async Task<(Task Mount, Hold Hold, int LogMark)> MountWithSuspendDuringSampleAsync(
        PreemptSpecHarness h, string hostKey, string operation, OnCancel onCancel = OnCancel.LikeProduction)
    {
        var hold = h.HRclone.HoldNext(onCancel, operation);
        var mount = h.Supervisor.RequestMountAsync(hostKey);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, $"the post-mount baseline sample's {operation} call of {hostKey}");
        var mark = h.Logger.Entries.Count;

        var suspend = SuspendCaller.Call(h.Supervisor);
        AssertSuspendCancelled(hold, $"the in-flight baseline {operation} call");

        if (onCancel == OnCancel.Ignore)
        {
            // The call returns a real answer, but only after its token was cancelled (decision 3: decided
            // by the token, never by the outcome).
            await PreemptSpecHarness.WaitForAsync(() => hold.TokenCancelled, "the held sample's token was never cancelled");
            hold.Release();
        }

        await h.FinishAsync(pump, $"the suspend waited for the hung baseline {operation} call", suspend, mount);
        await suspend;
        return (mount, hold, mark);
    }
}
