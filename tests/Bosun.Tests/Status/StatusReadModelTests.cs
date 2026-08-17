using Bosun.Configuration;
using Bosun.SessionMonitor;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Status.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Status;

/// <summary>
/// Deterministic coverage of the polling half of <see cref="StatusReadModel"/> (bs-ww9.6): the
/// timer cadence, start/stop lifecycle, the <see cref="IStatusReadModel.Changed"/> event, and the
/// session-count correlation. All driven by <see cref="FakeTimeProvider"/> -- no real clock, no
/// real thread hand-off, no real <c>IMountSupervisor</c>/<c>ISessionMonitor</c> (CLAUDE.md
/// worktree-safety rules).
/// </summary>
public sealed class StatusReadModelTests
{
    private sealed record Harness(
        StatusReadModel Model,
        FakeMountSupervisorSnapshotSource Supervisor,
        FakeSessionMonitor Sessions,
        FakeTimeProvider Time);

    private static Harness Build(BosunConfig config)
    {
        var time = new FakeTimeProvider();
        var supervisor = new FakeMountSupervisorSnapshotSource();
        var sessions = new FakeSessionMonitor();
        var configStore = new FakeHostConfigStore(config);
        var model = new StatusReadModel(supervisor, configStore, sessions, time, NullLogger<StatusReadModel>.Instance);
        return new Harness(model, supervisor, sessions, time);
    }

    private static HostMountSnapshot Mounted(string hostKey) => new()
    {
        HostKey = hostKey,
        State = MountState.Mounted,
        AdministrativelyEnabled = true,
    };

    [Fact]
    public void Current_is_seeded_synchronously_at_construction_before_Start_is_ever_called()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));

        // Mutating what the supervisor would return AFTER construction must not retroactively
        // change the seed -- proving the seed genuinely happened once, in the constructor, rather
        // than being computed lazily on first read of Current.
        h.Supervisor.SnapshotToReturn = [Mounted("prod")];

        Assert.Empty(h.Model.Current.Rows);
        Assert.Equal(1, h.Supervisor.GetSnapshotCallCount);
    }

    [Fact]
    public void Start_polls_at_the_documented_interval_and_picks_up_a_changed_snapshot()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Model.Start();
        h.Supervisor.SnapshotToReturn = [Mounted("prod")];

        // Not yet polled -- less than one full interval has elapsed.
        h.Time.Advance(StatusReadModel.PollInterval - TimeSpan.FromMilliseconds(1));
        Assert.Empty(h.Model.Current.Rows);

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Single(h.Model.Current.Rows);
        Assert.Equal(StatusCategory.Healthy, h.Model.Current.Rows[0].Category);
    }

    [Fact]
    public void Changed_fires_once_per_poll_and_never_before_Start_is_called()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        var fireCount = 0;
        h.Model.Changed += (_, _) => fireCount++;

        h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, fireCount);

        h.Model.Start();
        h.Time.Advance(StatusReadModel.PollInterval);
        Assert.Equal(1, fireCount);

        h.Time.Advance(StatusReadModel.PollInterval);
        Assert.Equal(2, fireCount);

        h.Time.Advance(StatusReadModel.PollInterval * 3);
        Assert.Equal(5, fireCount);
    }

    [Fact]
    public void Stop_halts_further_polling_and_Current_keeps_the_last_computed_snapshot()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Model.Start();
        h.Supervisor.SnapshotToReturn = [Mounted("prod")];
        h.Time.Advance(StatusReadModel.PollInterval);
        Assert.Single(h.Model.Current.Rows);
        var callsBeforeStop = h.Supervisor.GetSnapshotCallCount;

        h.Model.Stop();
        h.Supervisor.SnapshotToReturn = [];
        h.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(callsBeforeStop, h.Supervisor.GetSnapshotCallCount);
        Assert.Single(h.Model.Current.Rows); // unchanged -- no poll happened to clear it
    }

    [Fact]
    public void Start_is_idempotent_a_second_call_does_not_arm_a_second_timer()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Model.Start();
        h.Model.Start();

        h.Time.Advance(StatusReadModel.PollInterval);

        // 1 call from the constructor's seed + 1 from a single timer's first tick. A second,
        // independent timer created by the redundant Start() would make this 3.
        Assert.Equal(2, h.Supervisor.GetSnapshotCallCount);
    }

    [Fact]
    public void Session_counts_are_correlated_by_host_key_and_summed_across_multiple_sessions()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Supervisor.SnapshotToReturn = [Mounted("prod")];
        h.Sessions.SetSessions(
        [
            new SshSession { HostKey = "prod", ProcessId = 1, SocketState = SessionSocketState.Established, StartTime = DateTimeOffset.UnixEpoch },
            new SshSession { HostKey = "prod", ProcessId = 2, SocketState = SessionSocketState.Established, StartTime = DateTimeOffset.UnixEpoch },
            new SshSession { HostKey = "someone-else", ProcessId = 3, SocketState = SessionSocketState.Established, StartTime = DateTimeOffset.UnixEpoch },
        ]);
        h.Model.Start();

        h.Time.Advance(StatusReadModel.PollInterval);

        Assert.Equal(2, h.Model.Current.Rows.Single(r => r.HostKey == "prod").SessionCount);
    }

    [Fact]
    public void A_snapshot_host_missing_from_the_current_config_is_skipped_rather_than_throwing()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Supervisor.SnapshotToReturn = [Mounted("prod"), Mounted("ghost-host-not-in-config")];
        h.Model.Start();

        h.Time.Advance(StatusReadModel.PollInterval);

        var row = Assert.Single(h.Model.Current.Rows);
        Assert.Equal("prod", row.HostKey);
    }

    [Fact]
    public void Aggregate_health_on_the_snapshot_always_matches_DeriveAggregateHealth_over_its_own_rows()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Supervisor.SnapshotToReturn =
        [
            new HostMountSnapshot
            {
                HostKey = "prod",
                State = MountState.Ready,
                AdministrativelyEnabled = true,
                MountUnavailableReason = "WinFsp is not installed",
            },
        ];
        h.Model.Start();

        h.Time.Advance(StatusReadModel.PollInterval);

        Assert.Equal(AggregateHealth.Error, h.Model.Current.Health);
        Assert.Equal(StatusDerivation.DeriveAggregateHealth(h.Model.Current.Rows), h.Model.Current.Health);
    }

    [Fact]
    public void RecentTransitions_is_carried_through_verbatim_from_the_supervisor()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        var entry = new MountTransitionEntry
        {
            TimestampUtc = DateTimeOffset.UnixEpoch,
            HostKey = "prod",
            From = MountState.Probing,
            To = MountState.Ready,
            Trigger = "probe ok",
        };
        h.Supervisor.TransitionHistoryToReturn = [entry];
        h.Model.Start();

        h.Time.Advance(StatusReadModel.PollInterval);

        Assert.Same(entry, Assert.Single(h.Model.Current.RecentTransitions));
    }

    [Fact]
    public void Dispose_stops_polling()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var h = Build(HostFixtures.Build(HostFixtures.Global(), host));
        h.Model.Start();
        h.Time.Advance(StatusReadModel.PollInterval);
        var callsBeforeDispose = h.Supervisor.GetSnapshotCallCount;

        h.Model.Dispose();
        h.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(callsBeforeDispose, h.Supervisor.GetSnapshotCallCount);
    }
}
