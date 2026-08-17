using Bosun.Supervisor;

namespace Bosun.Status;

/// <summary>
/// One point-in-time capture of everything a UI needs to render (bs-ww9.6): every host's derived
/// status row, the aggregate health for the tray icon, and a copy of the supervisor's transition
/// history -- all computed together from the SAME poll of <see cref="IMountSupervisor"/>, so a row
/// and the aggregate health can never be read out of sync with each other.
/// </summary>
public sealed record StatusSnapshot
{
    public required IReadOnlyList<HostStatusRow> Rows { get; init; }

    public required AggregateHealth Health { get; init; }

    /// <summary>Newest-first, exactly as <see cref="IMountSupervisor.GetTransitionHistory"/>
    /// documents its own return value.</summary>
    public required IReadOnlyList<MountTransitionEntry> RecentTransitions { get; init; }

    /// <summary>When this snapshot was computed, from the injected <see cref="TimeProvider"/>.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    public static readonly StatusSnapshot Empty = new()
    {
        Rows = [],
        Health = AggregateHealth.Healthy,
        RecentTransitions = [],
        CapturedAtUtc = DateTimeOffset.MinValue,
    };
}

/// <summary>
/// The read-model between <see cref="IMountSupervisor"/> and any UI (bs-ww9.6). Polls the
/// supervisor on a timer (docs/ARCHITECTURE.md: the supervisor exposes no change event by design,
/// and polling avoids cross-thread marshalling problems -- see <c>Bosun.Status.StatusReadModel</c>'s
/// remarks) and exposes the latest derived <see cref="StatusSnapshot"/>. Pure data plus a plain
/// <see cref="EventHandler"/> -- no WPF types anywhere, so a WPF window, the tray icon, or a test
/// can all consume it identically.
/// </summary>
public interface IStatusReadModel : IDisposable
{
    /// <summary>The most recently computed snapshot. Never null -- seeded at construction time
    /// (before <see cref="Start"/> is ever called) so a consumer that reads this before starting
    /// the poll still gets a real, if immediately-stale, snapshot rather than needing to handle
    /// "not started yet" as a special case.</summary>
    StatusSnapshot Current { get; }

    /// <summary>Raised after every poll that produces a new <see cref="Current"/>. Fired on
    /// whatever thread the underlying <see cref="TimeProvider"/> timer callback runs on -- a WPF
    /// subscriber is responsible for marshalling to the UI thread itself (e.g.
    /// <c>Dispatcher.Invoke</c>), exactly as it would for any other non-WPF event source. Not
    /// raised by the constructor's initial seed of <see cref="Current"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Begins polling. Idempotent -- a second call while already started is a no-op.</summary>
    void Start();

    /// <summary>Stops polling. <see cref="Current"/> keeps returning the last snapshot computed
    /// before the stop. Idempotent.</summary>
    void Stop();
}
