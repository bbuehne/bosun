using Bosun.Configuration;
using Bosun.SessionMonitor;
using Bosun.Supervisor;
using Microsoft.Extensions.Logging;

namespace Bosun.Status;

/// <summary>
/// Real <see cref="IStatusReadModel"/> (bs-ww9.6): polls <see cref="IMountSupervisor.GetSnapshot"/>,
/// <see cref="IMountSupervisor.GetTransitionHistory"/>, <see cref="IHostConfigStore.Current"/>, and
/// <see cref="ISessionMonitor.GetActiveSessions"/> on a timer, runs each host through
/// <see cref="StatusDerivation.DeriveRow"/>, and publishes the result as <see cref="Current"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why polling, not a change event.</b> <see cref="IMountSupervisor"/> deliberately exposes no
/// "something changed" event (see its own remarks: commands and timer callbacks all funnel through
/// one internal channel, and <see cref="IMountSupervisor.GetSnapshot"/> is documented as a
/// point-in-time read from an arbitrary thread). Wiring a push event out of that channel would mean
/// either marshalling supervisor-internal state across threads on every single transition (exactly
/// the cross-thread-mutation hazard the channel exists to avoid) or debouncing/coalescing bursts of
/// transitions into UI-friendly updates -- which is just polling with extra steps and its own new
/// race conditions. Polling on a plain <see cref="TimeProvider"/> timer sidesteps all of that: each
/// tick does one clean, already-thread-safe read of three independent snapshot surfaces
/// (<see cref="IMountSupervisor.GetSnapshot"/>, <see cref="IMountSupervisor.GetTransitionHistory"/>,
/// <see cref="ISessionMonitor.GetActiveSessions"/>, each already documented as safe to call from any
/// thread) and composes them into one atomically-published <see cref="StatusSnapshot"/>.
/// </para>
/// <para>
/// <b>Why <see cref="PollInterval"/> is what it is.</b> This is a UI refresh cadence, not a
/// correctness timer -- nothing here participates in Invariant I1/I2. ADR-018's stated reason a
/// status window exists at all is "watching state change during a resume": the acceptance test is a
/// human watching the window while the machine wakes up, so the interval needs to feel live against
/// that observer, not against any protocol timeout. One second is comfortably faster than anything
/// it is reporting on -- the fastest thing that can happen is a probe timing out
/// (<c>global.probe_timeout_seconds</c>, default 5s) or a drain retry (<c>DrainRetryInterval</c>,
/// 5s) -- while staying cheap: every read polled is an in-memory snapshot with no I/O
/// (<see cref="IMountSupervisor.GetSnapshot"/>'s own remarks), so polling it every second costs
/// nothing meaningful even for a window nobody is looking at. Not sub-second, because nothing in the
/// system can change faster than that and it would just be wasted wakeups on a background timer that
/// (per ADR-018) is running even while the window is hidden.
/// </para>
/// </remarks>
public sealed class StatusReadModel : IStatusReadModel
{
    /// <summary>UI refresh cadence -- see the class remarks for why 1 second. Same category as
    /// <c>MountSupervisor.ReconciliationInterval</c>: a deliberately hardcoded constant, not
    /// config-driven (docs/CONFIG-SCHEMA.md has no setting for it, and it is not a value that feeds
    /// a safety decision).</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IMountSupervisor supervisor;
    private readonly IHostConfigStore configStore;
    private readonly ISessionMonitor sessionMonitor;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<StatusReadModel> logger;

    private ITimer? timer;
    private volatile StatusSnapshot current;

    public StatusReadModel(
        IMountSupervisor supervisor,
        IHostConfigStore configStore,
        ISessionMonitor sessionMonitor,
        TimeProvider timeProvider,
        ILogger<StatusReadModel> logger)
    {
        this.supervisor = supervisor;
        this.configStore = configStore;
        this.sessionMonitor = sessionMonitor;
        this.timeProvider = timeProvider;
        this.logger = logger;

        // Seeded synchronously so Current is a real snapshot even if a caller reads it before ever
        // calling Start() -- see IStatusReadModel.Current's remarks.
        current = Recompute();
    }

    public StatusSnapshot Current => current;

    public event EventHandler? Changed;

    public void Start() => timer ??= timeProvider.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        current = Recompute();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private StatusSnapshot Recompute()
    {
        var snapshots = supervisor.GetSnapshot();
        var hosts = configStore.Current.Hosts;
        var sessionCounts = BuildSessionCounts(sessionMonitor.GetActiveSessions());

        var rows = new List<HostStatusRow>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (!hosts.TryGetValue(snapshot.HostKey, out var hostConfig))
            {
                // IMountSupervisor's own remarks: a config reload that adds/removes/changes a host
                // while running is not yet handled (requires an app restart). This should therefore
                // never actually happen in practice; skip defensively rather than throwing out of a
                // background poll timer, but log it because it means the supervisor's host set and
                // the config store's host set have drifted, which is itself worth knowing about.
                logger.LogWarning(
                    "Status poll: {HostKey} is in the supervisor's snapshot but not in the current config; skipping its row",
                    snapshot.HostKey);
                continue;
            }

            var sessionCount = sessionCounts.GetValueOrDefault(snapshot.HostKey);
            rows.Add(StatusDerivation.DeriveRow(snapshot, hostConfig, sessionCount));
        }

        return new StatusSnapshot
        {
            Rows = rows,
            Health = StatusDerivation.DeriveAggregateHealth(rows),
            RecentTransitions = supervisor.GetTransitionHistory(),
            CapturedAtUtc = timeProvider.GetUtcNow(),
        };
    }

    private static Dictionary<string, int> BuildSessionCounts(IReadOnlyList<SshSession> sessions)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            counts[session.HostKey] = counts.GetValueOrDefault(session.HostKey) + 1;
        }

        return counts;
    }
}
