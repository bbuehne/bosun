using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-ww9.2 / ADR-018: the bounded in-memory transition history that lets the E9 window show
/// "what happened while I was asleep" without ever parsing Bosun's own log files.
/// </summary>
public sealed class TransitionHistoryTests
{
    [Fact]
    public async Task Transitions_are_recorded_in_order_with_correct_from_to_and_trigger()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        // StartAsync on an on-demand host: Disabled -> Probing -> Ready (2 transitions; on-demand
        // never auto-mounts on becoming Ready -- rule 6).
        await harness.StartAsync();

        // Ready -> Mounting -> Mounted (2 more).
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var history = harness.Supervisor.GetTransitionHistory();

        Assert.Equal(4, history.Count);

        // Newest-first, per IMountSupervisor.GetTransitionHistory's documented contract.
        Assert.Equal("archive", history[0].HostKey);
        Assert.Equal(MountState.Mounting, history[0].From);
        Assert.Equal(MountState.Mounted, history[0].To);
        Assert.False(string.IsNullOrWhiteSpace(history[0].Trigger));

        Assert.Equal(MountState.Ready, history[1].From);
        Assert.Equal(MountState.Mounting, history[1].To);

        Assert.Equal(MountState.Probing, history[2].From);
        Assert.Equal(MountState.Ready, history[2].To);

        Assert.Equal(MountState.Disabled, history[3].From);
        Assert.Equal(MountState.Probing, history[3].To);

        Assert.All(history, e => Assert.Equal("archive", e.HostKey));
    }

    [Fact]
    public async Task Timestamps_come_from_the_injected_clock()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(
            HostFixtures.Build(HostFixtures.Global(), host),
            start: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await harness.StartAsync();

        var afterStart = harness.Time.GetUtcNow();
        Assert.All(harness.Supervisor.GetTransitionHistory(), e => Assert.Equal(afterStart, e.TimestampUtc));

        await harness.AdvanceAsync(TimeSpan.FromMinutes(90));
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var afterMount = harness.Time.GetUtcNow();
        Assert.Equal(afterStart.AddMinutes(90), afterMount);

        var history = harness.Supervisor.GetTransitionHistory();
        // The two newest entries (the mount) carry the post-advance timestamp; the clock never
        // moved backwards and no entry predates the start of the test.
        Assert.Equal(afterMount, history[0].TimestampUtc);
        Assert.Equal(afterMount, history[1].TimestampUtc);
        Assert.All(history, e => Assert.True(e.TimestampUtc >= afterStart));
    }

    [Fact]
    public async Task Returned_snapshot_is_immutable_later_transitions_do_not_change_it()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();

        var before = harness.Supervisor.GetTransitionHistory();
        Assert.Equal(2, before.Count);

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // The earlier snapshot is untouched by transitions recorded after it was taken.
        Assert.Equal(2, before.Count);
        Assert.Equal(MountState.Probing, before[0].From);

        var after = harness.Supervisor.GetTransitionHistory();
        Assert.Equal(4, after.Count);
    }

    [Fact]
    public async Task History_is_genuinely_bounded_oldest_entries_fall_off_newest_survive()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();

        // StartAsync's Disabled -> Probing is the very first transition ever recorded; capture it
        // so we can later prove it has fallen off the ring buffer.
        var firstEverEntry = Assert.Single(harness.Supervisor.GetTransitionHistory(), e => e.From == MountState.Disabled);

        var capacity = MountSupervisor.TransitionHistoryCapacity;

        // Each mount+unmount round trip records 6 transitions (Ready->Mounting->Mounted, then
        // Mounted->Draining->Disabled->Probing->Ready via the auto re-enable after an on-demand
        // drain -- see DrainAndForcedUnmountTests for that same path). Comfortably overflow
        // capacity so the ring buffer has definitely wrapped at least once.
        var roundTrips = (capacity / 6) + 10;
        for (var i = 0; i < roundTrips; i++)
        {
            await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
            await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        }

        var history = harness.Supervisor.GetTransitionHistory();

        // Bounded: never exceeds capacity even though far more transitions than that occurred.
        Assert.Equal(capacity, history.Count);

        // Newest survives: the very last transition recorded is the most recent unmount's auto
        // re-enable landing back at Ready.
        Assert.Equal(MountState.Probing, history[0].From);
        Assert.Equal(MountState.Ready, history[0].To);

        // Oldest falls off: StartAsync's very first transition is long gone from the buffer. (Not
        // "no entry has From == Disabled" -- Disabled -> Probing recurs on every unmount cycle's
        // auto re-enable, so plenty of those legitimately survive; it's specifically the very
        // first one, identified by reference, that must be gone.)
        Assert.DoesNotContain(history, e => e == firstEverEntry);
    }
}
