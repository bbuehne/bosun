using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// bs-4ca / ADR-023 decision 8: "a probe that returns while the supervisor's own token is cancelled is
/// shutdown, whatever it returned ... a host one failure short of its threshold is not drained as Automatic
/// while Bosun exits." Driven through the production loop (<see cref="MountSupervisor.RunAsync"/>), whose
/// token is the supervisor's own token. Written from the ADR, not from the implementation.
/// </summary>
/// <remarks>
/// ADR-004 / <c>IMountSupervisor.StopAsync</c>: shutdown does not proactively unmount (process exit tears the
/// rcd child down). So the correct observation is: no drain begun, no <c>mount/unmount</c> sent, the failure
/// count unchanged.
/// </remarks>
public sealed class ShutdownCancellationSpecTests
{
    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));

    private static async Task<PreemptSpecHarness> MountedOneShortAsync(bool deep)
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        if (deep)
        {
            h.Probe.SetDeep("alpha", DeepProbeOutcome.Failed, times: 1);
            await h.AdvanceAsync(TimeSpan.FromSeconds(300));
            Assert.Equal(1, h.Snapshot("alpha").ConsecutiveDeepProbeFailures);
            await h.AdvanceAsync(TimeSpan.FromSeconds(299));
        }
        else
        {
            h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("alpha"), ShallowProbeOutcome.ConnectionRefused, times: 2);
            await h.AdvanceAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(2, h.Snapshot("alpha").ConsecutiveMountedFailures);
        }

        Assert.Equal(MountState.Mounted, h.State("alpha"));
        return h;
    }

    private static async Task ShutDownDuringHeldProbeAsync(PreemptSpecHarness h, Hold hold, TimeSpan fire, object? releaseWith)
    {
        using var shutdown = new CancellationTokenSource();
        var loop = Task.Run(() => h.Supervisor.RunAsync(shutdown.Token));
        h.FireTimers(fire);
        await PreemptSpecHarness.ArrivedAsync(hold, "the mounted probe");

        await shutdown.CancelAsync();
        if (hold.OnCancel == OnCancel.Ignore)
        {
            Assert.True(hold.TokenCancelled, "the probe was not handed a token linked to the supervisor's own");
            hold.Release(releaseWith);
        }

        await Task.WhenAny(loop, Task.Delay(PreemptSpecHarness.Guard));
        Assert.True(loop.IsCompleted, "the supervisor loop did not stop on shutdown");
    }

    private static void AssertNothingDrained(PreemptSpecHarness h)
    {
        Assert.Empty(h.DrainCauses("alpha"));
        Assert.Equal(0, h.HRclone.Count("unmount", "A:"));
        Assert.True(h.IsMountedInRclone("A:"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 8 for the shallow probe, as <c>HostProbe</c> really behaves (returns
    /// <c>Cancelled</c>, does not throw).
    /// <b>Catches:</b> bs-4ca as filed: the cancelled probe counted as the third failure, so quitting Bosun
    /// drains the host as <c>Automatic</c> and toasts a failure that did not happen.
    /// </summary>
    [Fact]
    public async Task Shutdown_during_a_mounted_shallow_probe_does_not_count_it_as_a_failure()
    {
        var h = await MountedOneShortAsync(deep: false);
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"));

        await ShutDownDuringHeldProbeAsync(h, hold, TimeSpan.FromSeconds(60), null);

        AssertNothingDrained(h);
        Assert.Equal(2, h.Snapshot("alpha").ConsecutiveMountedFailures);
    }

    /// <summary>
    /// <b>Protects:</b> decision 8 for the deep probe (threshold 2).
    /// <b>Catches:</b> the same defect on the deep handler.
    /// </summary>
    [Fact]
    public async Task Shutdown_during_a_mounted_deep_probe_does_not_count_it_as_a_failure()
    {
        var h = await MountedOneShortAsync(deep: true);
        var hold = h.HProbe.HoldNextDeep("alpha");

        await ShutDownDuringHeldProbeAsync(h, hold, TimeSpan.FromSeconds(1), null);

        AssertNothingDrained(h);
        Assert.Equal(1, h.Snapshot("alpha").ConsecutiveDeepProbeFailures);
    }

    /// <summary>
    /// <b>Protects:</b> decision 8's "whatever it returned": a probe that comes back with a genuine-looking
    /// <c>Timeout</c> after the supervisor's token was cancelled is still shutdown.
    /// <b>Catches:</b> a fix that special-cases the outcome <c>Cancelled</c> instead of checking the token.
    /// </summary>
    [Fact]
    public async Task A_probe_that_returns_Timeout_after_shutdown_began_is_not_counted()
    {
        var h = await MountedOneShortAsync(deep: false);
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"), OnCancel.Ignore);

        await ShutDownDuringHeldProbeAsync(h, hold, TimeSpan.FromSeconds(60), ShallowProbeOutcome.Timeout);

        AssertNothingDrained(h);
        Assert.Equal(2, h.Snapshot("alpha").ConsecutiveMountedFailures);
    }
}
