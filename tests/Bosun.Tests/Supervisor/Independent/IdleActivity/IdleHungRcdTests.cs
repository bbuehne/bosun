using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 7: "Every such poll runs in the supervisor's serialised channel
/// under one probe timeout (<c>global.probe_timeout_seconds</c>), so a hung rcd cannot stall it
/// longer than a hung probe can." §5: the channel is single-threaded, so a poll that never returns
/// stops every host, not just this one.
/// </summary>
/// <remarks>
/// <para>
/// <b>How this stays deterministic.</b> The rc double's hung calls block until the token they were
/// given is cancelled. The test does not sleep: it waits for the call to start, then waits for the
/// supervisor to have armed a timer on the INJECTED clock that is due within one probe timeout of
/// the call starting (<see cref="Configuration.Fakes.FakeTimeProvider.WhenTimerDueWithinAsync"/>),
/// and only then advances the fake clock. The wall-clock <see cref="IdleActivityHarness.Guard"/> is
/// reached only if the supervisor is stuck; a passing run never waits on it.
/// </para>
/// <para>
/// A supervisor that bounds the poll with a real-time timer, or not at all, never arms such a timer:
/// the test then fails on the guard with a message saying so, instead of hanging the run.
/// </para>
/// </remarks>
public sealed class IdleHungRcdTests
{
    private const int ProbeTimeout = 5;

    /// <summary>
    /// The idle period equals the probe interval (60 s), so at +60 the periodic sample and the idle
    /// fire are due in the same tick, and rcd hangs on every <c>vfs/*</c> call from +59. Expected:
    /// each hung poll is abandoned within one probe timeout of starting, the tick as a whole within
    /// two (one for the sample, one for the fire), the channel drains, the host stays Mounted
    /// (fail closed), and once rcd answers again the idle unmount happens normally. Bug classes: a
    /// poll with no timeout (the channel, and so every host, stops for good); a timeout on the wall
    /// clock rather than the injected <see cref="TimeProvider"/>; the cancelled poll's
    /// <see cref="OperationCanceledException"/> read as shutdown and killing the pump (bs-x57);
    /// a timed-out poll read as "no activity" and the drive unmounted with writes pending.
    /// </summary>
    [Fact]
    public async Task A_hung_rcd_during_a_sample_and_a_fire_in_the_same_tick_stalls_the_channel_at_most_one_probe_timeout_per_poll()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 60, mountedProbeIntervalSeconds: 60, probeTimeoutSeconds: ProbeTimeout);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(59);

        harness.Rclone.HangVfs = true;
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var tickAt = harness.Time.GetUtcNow();
        var drain = harness.Supervisor.DrainAsync();

        var released = 0;
        while (true)
        {
            var entered = harness.Rclone.HungEntries.WaitAsync(IdleActivityHarness.Guard);
            var first = await Task.WhenAny(drain, entered);
            if (first == drain)
            {
                break;
            }

            if (!await entered)
            {
                Assert.Fail(
                    $"the supervisor's channel is stuck: {released} hung vfs call(s) released, and in {IdleActivityHarness.Guard.TotalSeconds}s " +
                    $"of wall time neither another vfs call started nor did the channel drain.{Environment.NewLine}{harness.Dump("archive")}");
            }

            var call = harness.Rclone.HungCalls[released];
            var armed = harness.Time.WhenTimerDueWithinAsync(call.EnteredAt, call.EnteredAt + TimeSpan.FromSeconds(ProbeTimeout));
            if (await Task.WhenAny(armed, Task.Delay(IdleActivityHarness.Guard)) != armed)
            {
                Assert.Fail(
                    $"{call.Endpoint} hung at +{(call.EnteredAt - DateTimeOffset.UnixEpoch).TotalSeconds}s and nothing on the injected clock " +
                    $"is due to end it within probe_timeout_seconds ({ProbeTimeout}s). Rule 7: every such poll runs under one probe timeout.");
            }

            harness.Time.Advance(TimeSpan.FromSeconds(ProbeTimeout));
            released++;
            Assert.True(released <= 6, $"more than six hung vfs calls in one tick.{Environment.NewLine}{harness.Dump("archive")}");
        }

        await drain.WaitAsync(IdleActivityHarness.Guard);

        Assert.True(harness.Rclone.HungCalls.Count >= 1, "the scenario never reached a vfs call; the test proves nothing");
        var stalled = harness.Time.GetUtcNow() - tickAt;
        Assert.True(
            stalled <= TimeSpan.FromSeconds(2 * ProbeTimeout),
            $"one tick (a sample and a fire) held the channel for {stalled.TotalSeconds}s of fake time; at most two probe timeouts " +
            $"({2 * ProbeTimeout}s) are documented. Hung calls: {string.Join(", ", harness.Rclone.HungCalls.Select(c => c.Endpoint))}");
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));

        // rcd answers again: the rule carries on, so the channel and the idle clock are both alive.
        harness.Rclone.HangVfs = false;
        var recoveredAt = harness.Now;
        await harness.AdvanceSecondsAsync((3 * 60) + 5);
        Assert.True(
            harness.DrainTime("archive", DrainCause.IdleTimeout, notBefore: recoveredAt) is not null,
            $"after rcd recovered the idle unmount never happened.{Environment.NewLine}{harness.Dump("archive")}");
    }
}
