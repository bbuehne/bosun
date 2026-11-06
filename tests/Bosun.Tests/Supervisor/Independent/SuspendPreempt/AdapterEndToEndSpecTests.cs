using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.SystemEventIntegration;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// bs-4in's acceptance case end to end: <c>SystemEventSupervisorAdapter.WaitForSuspendDrainAsync</c> over a
/// REAL <see cref="MountSupervisor"/> running its production loop (<see cref="MountSupervisor.RunAsync"/>),
/// with one recurring check hung on an rc call or probe that only cancellation can release. Written from
/// ADR-023 and docs/ARCHITECTURE.md §3 ("A suspend does not wait for routine checks"), not from the
/// implementation.
/// </summary>
/// <remarks>
/// <para>
/// The adapter races the supervisor against <c>Task.Delay(budget, timeProvider)</c>. The fake clock is never
/// advanced in the first test, so the budget can never win: if the adapter returns <c>true</c>, the drain
/// really confirmed with the hung check still in the channel's way. If the suspend waited for the check, the
/// adapter would wait forever on the fake clock, and the guard turns that into a failure.
/// </para>
/// <para>
/// No <c>SystemEvents</c>, no real rclone, no drive letter: the event source is the existing fake, and the
/// supervisor's collaborators are the in-memory doubles in this folder.
/// </para>
/// </remarks>
public sealed class AdapterEndToEndSpecTests
{
    public enum HungCheck
    {
        MountedShallowProbe,
        MountedVfsSample,
        MountedDeepProbe,
        IdleUnmountFire,
        Reconciliation,
    }

    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));

    private static SystemEventSupervisorAdapter AdapterFor(PreemptSpecHarness h) =>
        new(new FakeSystemEventSource(), h.Supervisor, h.Store, h.Time, NullLogger<SystemEventSupervisorAdapter>.Instance);

    /// <summary>
    /// <b>Protects:</b> bs-4in / ADR-023 decision 2, for each of the five preemptible checks, through the real
    /// adapter and the real loop: a hung routine check no longer makes the suspend handler wait.
    /// <b>Catches:</b> any of the five ticks not running under the preemption token (the suspend queues behind
    /// it and the handler returns only when the budget elapses, with the drive still mounted); a preemption
    /// that does not reach the hung call itself (e.g. the <c>vfs/stats</c> sample after the shallow probe).
    /// </summary>
    [Theory]
    [InlineData(HungCheck.MountedShallowProbe)]
    [InlineData(HungCheck.MountedVfsSample)]
    [InlineData(HungCheck.MountedDeepProbe)]
    [InlineData(HungCheck.IdleUnmountFire)]
    [InlineData(HungCheck.Reconciliation)]
    public async Task The_suspend_handler_confirms_the_drain_without_waiting_for_a_hung_recurring_check(HungCheck check)
    {
        var onDemand = check is HungCheck.MountedVfsSample or HungCheck.IdleUnmountFire;
        var host = onDemand
            ? HostFixtures.OnDemand("alpha", idleUnmountSeconds: check == HungCheck.IdleUnmountFire ? 90 : 600, drive: "A:")
            : HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:");
        var h = NewHarness(host);
        await h.StartAsync();
        if (onDemand)
        {
            await h.RunAsync(() => h.Supervisor.RequestMountAsync("alpha"));
        }

        Assert.Equal(MountState.Mounted, h.State("alpha"));

        (Hold hold, TimeSpan fire) = check switch
        {
            HungCheck.MountedShallowProbe => (h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha")), TimeSpan.FromSeconds(60)),
            HungCheck.MountedVfsSample => (h.HRclone.HoldNext(OnCancel.LikeProduction, "vfslist", "vfsstats", "vfsqueue"), TimeSpan.FromSeconds(60)),
            HungCheck.MountedDeepProbe => await PrepareAsync(h, TimeSpan.FromSeconds(299), () => h.HProbe.HoldNextDeep("alpha"), TimeSpan.FromSeconds(1)),
            HungCheck.IdleUnmountFire => await PrepareAsync(h, TimeSpan.FromSeconds(89.9), () => h.HRclone.HoldNext(OnCancel.LikeProduction, "vfslist", "vfsstats", "vfsqueue"), TimeSpan.FromSeconds(0.2)),
            HungCheck.Reconciliation => await PrepareAsync(h, TimeSpan.FromSeconds(29.9), () => h.HRclone.HoldNext(OnCancel.LikeProduction, "version", "listmounts"), TimeSpan.FromSeconds(0.2)),
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };

        using var shutdown = new CancellationTokenSource();
        var loop = Task.Run(() => h.Supervisor.RunAsync(shutdown.Token));
        try
        {
            h.FireTimers(fire);
            await PreemptSpecHarness.ArrivedAsync(hold, check.ToString());

            using var adapter = AdapterFor(h);
            var handled = adapter.WaitForSuspendDrainAsync();
            await Task.WhenAny(handled, Task.Delay(PreemptSpecHarness.Guard));
            Assert.True(handled.IsCompleted, $"the suspend handler waited behind the hung {check} (the fake clock never moves, so only the drain can release it)");
            Assert.True(await handled, "the suspend handler reported the budget elapsed");

            Assert.True(hold.TokenCancelled, $"the hung {check} was never cancelled");
            Assert.False(h.IsMountedInRclone("A:"));
            Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
            Assert.Empty(h.Errors);
        }
        finally
        {
            await shutdown.CancelAsync();
            await Task.WhenAny(loop, Task.Delay(PreemptSpecHarness.Guard));
        }
    }

    /// <summary>
    /// <b>Protects:</b> ADR-023's admitted residual and §3: a drain is not preempted, so a hung
    /// <c>mount/unmount</c> ahead of the suspend still holds it -- and "the adapter's race against the budget
    /// (<c>WaitForSuspendDrainAsync</c>) stays as the backstop for it". The supervisor finishes the job once the
    /// drain returns.
    /// <b>Catches:</b> the backstop removed or bypassed because "suspend no longer waits" (the handler would hang
    /// on <c>SystemEvents</c>' thread); or the deferred suspend never running after the budget.
    /// </summary>
    [Fact]
    public async Task A_hung_drain_ahead_of_the_suspend_is_still_bounded_by_the_budget_backstop()
    {
        var h = NewHarness(
            HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"),
            HostFixtures.Persistent("bravo", probeIntervalSeconds: 60, drive: "B:"));
        await h.StartAsync();

        using var shutdown = new CancellationTokenSource();
        var loop = Task.Run(() => h.Supervisor.RunAsync(shutdown.Token));
        try
        {
            var unmount = h.HRclone.HoldNext(OnCancel.Ignore, "unmount");
            var userUnmount = h.Supervisor.RequestUnmountAsync("alpha");
            await PreemptSpecHarness.ArrivedAsync(unmount, "alpha's user-unmount drain");

            using var adapter = AdapterFor(h);
            var handled = adapter.WaitForSuspendDrainAsync(); // its budget timer exists once this returns
            Assert.False(handled.IsCompleted);
            h.FireTimers(TimeSpan.FromSeconds(3));
            await Task.WhenAny(handled, Task.Delay(PreemptSpecHarness.Guard));
            Assert.True(handled.IsCompleted, "the budget did not bound the suspend handler");
            Assert.False(await handled, "the handler claimed the drain confirmed while the drain ahead of it was hung");
            Assert.False(unmount.CancellationRequested, "the suspend cancelled a drain"); // pool-independent: bs-s30

            unmount.Release();
            await Task.WhenAny(userUnmount, Task.Delay(PreemptSpecHarness.Guard));

            // The channel is FIFO: a command queued now completes only after the deferred suspend has run.
            var marker = h.Supervisor.RequestRetryNowAsync("bravo");
            await Task.WhenAny(marker, Task.Delay(PreemptSpecHarness.Guard));
            Assert.True(marker.IsCompleted, "the channel never drained after the hung drain returned");

            Assert.False(h.IsMountedInRclone("B:"), "the suspend never ran after the drain ahead of it returned");
        }
        finally
        {
            await shutdown.CancelAsync();
            await Task.WhenAny(loop, Task.Delay(PreemptSpecHarness.Guard));
        }
    }

    private static async Task<(Hold, TimeSpan)> PrepareAsync(PreemptSpecHarness h, TimeSpan advance, Func<Hold> arm, TimeSpan fire)
    {
        await h.AdvanceAsync(advance);
        return (arm(), fire);
    }
}
