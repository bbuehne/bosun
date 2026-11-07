using Bosun.Rclone;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// Self-test of the fake (bs-6lu): <see cref="HoldableRclone"/> models <c>rclone rcd</c>'s single mount lock
/// (<c>mountMu</c>, <c>cmd/mountlib/rc.go</c>, v1.75.0), which <c>mount/mount</c>, <c>mount/unmount</c> and
/// <c>mount/listmounts</c> hold for their whole run while <c>core/version</c>, <c>operations/list</c> and
/// <c>vfs/*</c> do not. The supervisor tests that rely on the lock (the suspend sweep) are only as good as
/// this model, so it is pinned here without a supervisor in the way.
/// </summary>
/// <remarks>
/// No clock, no sleeps: a queued call cannot complete without a thread hop, so "has not completed" is
/// checkable straight after the call is made; "has completed" is awaited with the harness guard.
/// </remarks>
public sealed class HoldableRcloneMountLockTests
{
    private static readonly TimeSpan Guard = PreemptSpecHarness.Guard;

    private static (HoldableRclone Rclone, RcloneDouble Inner) New()
    {
        var inner = new RcloneDouble(new SupervisorCallLog());
        inner.SeedMount("M:", "bosun-example:/a");
        inner.SeedMount("N:", "bosun-example:/b");
        return (new HoldableRclone(inner, new FakeTimeProvider()), inner);
    }

    private static RcloneMountRequest MountRequest(string drive) =>
        new() { Fs = "bosun-example:/c", MountPoint = drive, VfsCacheMode = "writes" };

    private static Task StartMountLockCall(HoldableRclone rclone, string operation) => operation switch
    {
        "mount" => rclone.MountAsync(MountRequest("P:"), CancellationToken.None),
        "unmount" => rclone.UnmountAsync("N:", CancellationToken.None),
        "listmounts" => rclone.ListMountsAsync(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static async Task ArrivedAsync(Hold hold) => await hold.WhenArrived.WaitAsync(Guard);

    private static async Task ExecutingAsync(Hold hold) => await hold.WhenExecuting.WaitAsync(Guard);

    /// <summary>
    /// <b>Protects:</b> the lock itself: while any one of the three mount/* calls is running inside rcd, any
    /// other mount/* call is queued. The request is on the wire (recorded) but has not run, and the mount
    /// table is unchanged until the first call ends.
    /// <b>Catches:</b> a fake with no lock (the false assumption that rcd runs mount/* calls in parallel).
    /// </summary>
    [Theory]
    [InlineData("mount", "unmount")]
    [InlineData("mount", "listmounts")]
    [InlineData("unmount", "mount")]
    [InlineData("unmount", "unmount")]
    [InlineData("unmount", "listmounts")]
    [InlineData("listmounts", "mount")]
    [InlineData("listmounts", "unmount")]
    [InlineData("listmounts", "listmounts")]
    public async Task A_running_mount_call_holds_every_other_mount_call_in_a_queue(string running, string queued)
    {
        var (rclone, inner) = New();
        var first = rclone.HoldNext(OnCancel.Ignore, running);
        var firstCall = StartMountLockCall(rclone, running);
        await ArrivedAsync(first);
        await ExecutingAsync(first);

        var second = StartMountLockCall(rclone, queued);

        // On the wire, not running.
        Assert.Equal(2, rclone.Calls.Count);
        Assert.True(rclone.RcdMountLockHeld);
        Assert.False(second.IsCompleted);
        Assert.NotNull(inner.FsAt("N:"));
        Assert.Null(inner.FsAt("P:"));

        first.Release();
        await firstCall.WaitAsync(Guard);
        await second.WaitAsync(Guard);

        Assert.False(rclone.RcdMountLockHeld);
    }

    /// <summary>
    /// <b>Protects:</b> the other half of the model: the calls that do NOT take the lock still run while it is
    /// held, so a test is not made to wait for rcd where the real thing does not.
    /// <b>Catches:</b> a lock applied to every call (version, the VFS sampler and the deep probe's
    /// operations/list would all hang behind a hung unmount).
    /// </summary>
    [Fact]
    public async Task Version_list_and_vfs_calls_do_not_wait_for_the_mount_lock()
    {
        var (rclone, _) = New();
        var hung = rclone.HoldNext(OnCancel.Ignore, "unmount");
        var hungCall = rclone.UnmountAsync("M:", CancellationToken.None);
        await ExecutingAsync(hung);
        Assert.True(rclone.RcdMountLockHeld);

        var version = await rclone.GetVersionAsync(CancellationToken.None).WaitAsync(Guard);
        var list = await rclone.ListAsync("bosun-example:/a", string.Empty, CancellationToken.None).WaitAsync(Guard);
        var vfsList = await rclone.ListVfsAsync(CancellationToken.None).WaitAsync(Guard);
        var stats = await rclone.GetVfsStatsAsync("bosun-example:/a", CancellationToken.None).WaitAsync(Guard);
        var queue = await rclone.GetVfsQueueAsync("bosun-example:/a", CancellationToken.None).WaitAsync(Guard);

        Assert.NotNull(version);
        Assert.Empty(list);
        Assert.NotNull(vfsList);
        Assert.NotNull(stats);
        Assert.Empty(queue);
        Assert.False(hungCall.IsCompleted);

        hung.Release();
        await hungCall.WaitAsync(Guard);
    }

    /// <summary>
    /// <b>Protects:</b> "a call Bosun abandons keeps the lock" (the unmount and listmounts handlers ignore
    /// their context). The caller gets its timeout at once, rcd keeps going: the next call stays queued until
    /// rcd's work for the abandoned one ends, and that work then takes effect with nobody listening.
    /// <b>Catches:</b> a fake that frees the lock when the client gives up; one that never applies the late
    /// work.
    /// </summary>
    [Fact]
    public async Task An_abandoned_call_keeps_the_lock_until_rcd_finishes_it_and_then_takes_effect()
    {
        var (rclone, inner) = New();
        var hung = rclone.HoldNext(OnCancel.Ignore, "unmount");
        var hungCall = rclone.UnmountAsync("M:", CancellationToken.None);
        await ExecutingAsync(hung);
        var behind = rclone.ListMountsAsync(CancellationToken.None);

        hung.Abandon(new RcloneRcTimeoutException("mount/unmount", RcloneClient.UnmountTimeout));

        await Assert.ThrowsAsync<RcloneRcTimeoutException>(() => hungCall.WaitAsync(Guard));
        Assert.True(rclone.RcdMountLockHeld);
        Assert.False(behind.IsCompleted);
        Assert.NotNull(inner.FsAt("M:")); // rcd has not got there yet

        hung.FinishInRcd();
        var table = await behind.WaitAsync(Guard);

        Assert.Null(inner.FsAt("M:")); // the abandoned unmount did happen, late
        Assert.DoesNotContain(table, m => m.MountPoint == "M:"); // and the queued listmounts saw it
        Assert.False(rclone.RcdMountLockHeld);
    }

    /// <summary>
    /// <b>Protects:</b> the other failure shape: rcd ANSWERS with an error. The handler has returned, so the
    /// lock is free at once and the call did nothing to the table.
    /// <b>Catches:</b> an error response that strands the lock (every later mount/* call hangs); one that still
    /// applies the call.
    /// </summary>
    [Fact]
    public async Task A_call_rcd_answers_with_an_error_frees_the_lock_and_changes_nothing()
    {
        var (rclone, inner) = New();
        var failing = rclone.HoldNext(OnCancel.Ignore, "unmount");
        var failingCall = rclone.UnmountAsync("M:", CancellationToken.None);
        await ExecutingAsync(failing);
        var behind = rclone.ListMountsAsync(CancellationToken.None);

        failing.Fail(new RcloneRcException("mount/unmount", 500, "rcd could not unmount"));

        await Assert.ThrowsAsync<RcloneRcException>(() => failingCall.WaitAsync(Guard));
        var table = await behind.WaitAsync(Guard);
        Assert.Contains(table, m => m.MountPoint == "M:");
        Assert.NotNull(inner.FsAt("M:"));
        Assert.False(rclone.RcdMountLockHeld);
    }

    /// <summary>
    /// <b>Protects:</b> a held <see cref="OnCancel.Ignore"/> call stays hung, and keeps the lock, when its
    /// caller's token is cancelled: cancellation alone does not stop rcd's handler. Releasing it later lets
    /// the queue move.
    /// <b>Catches:</b> a fake that frees the lock on caller cancellation for a call that ignores it.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_caller_of_an_ignoring_call_does_not_free_the_lock()
    {
        var (rclone, _) = New();
        using var cts = new CancellationTokenSource();
        var hung = rclone.HoldNext(OnCancel.Ignore, "unmount");
        var hungCall = rclone.UnmountAsync("M:", cts.Token);
        await ExecutingAsync(hung);
        var behind = rclone.ListMountsAsync(CancellationToken.None);

        await cts.CancelAsync();

        Assert.True(hung.IsPending);
        Assert.True(rclone.RcdMountLockHeld);
        Assert.False(behind.IsCompleted);

        hung.Release();
        await hungCall.WaitAsync(Guard);
        await behind.WaitAsync(Guard);
    }

    /// <summary>
    /// <b>Protects:</b> the documented gap, so it cannot drift unnoticed: a held call under
    /// <see cref="OnCancel.LikeProduction"/> ends when its token is cancelled (the hang itself is defined to end
    /// there), and that frees the lock.
    /// <b>Catches:</b> a change that strands the lock for every such test (preempted mount, reconciliation).
    /// </summary>
    [Fact]
    public async Task A_call_that_ends_on_cancellation_frees_the_lock()
    {
        var (rclone, _) = New();
        using var cts = new CancellationTokenSource();
        var held = rclone.HoldNext(OnCancel.LikeProduction, "listmounts");
        var heldCall = rclone.ListMountsAsync(cts.Token);
        await ExecutingAsync(held);
        var behind = rclone.UnmountAsync("N:", CancellationToken.None);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heldCall.WaitAsync(Guard));
        await behind.WaitAsync(Guard);
        Assert.False(rclone.RcdMountLockHeld);
    }

    /// <summary>
    /// <b>Protects:</b> arrival, not execution, is what the record and the hold report: a request queued behind
    /// the lock is recorded with the time it was SENT, and a hold armed for it fires <c>WhenArrived</c> then,
    /// but <c>WhenExecuting</c> only when it gets the lock.
    /// <b>Catches:</b> a fake that records or signals arrival only once rcd runs the call, which would make
    /// "all requests left at arrival" untestable.
    /// </summary>
    [Fact]
    public async Task A_queued_request_is_recorded_and_arrives_at_once_but_executes_only_when_it_gets_the_lock()
    {
        var (rclone, _) = New();
        var first = rclone.HoldNext(OnCancel.Ignore, "unmount");
        var second = rclone.HoldNext(OnCancel.Ignore, "unmount");
        var firstCall = rclone.UnmountAsync("M:", CancellationToken.None);
        var secondCall = rclone.UnmountAsync("N:", CancellationToken.None);

        await ArrivedAsync(first);
        await ArrivedAsync(second);
        Assert.Equal(1, rclone.Count("unmount", "M:"));
        Assert.Equal(1, rclone.Count("unmount", "N:"));
        await ExecutingAsync(first);
        Assert.False(second.IsExecuting);

        first.Release();
        await firstCall.WaitAsync(Guard);
        await ExecutingAsync(second);
        second.Release();
        await secondCall.WaitAsync(Guard);
    }
}
