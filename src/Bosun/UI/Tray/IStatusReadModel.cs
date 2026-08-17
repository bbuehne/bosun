namespace Bosun.UI.Tray;

/// <summary>
/// THE SEAM between bs-ww9.6 (E9c, the status read-model -- causal per-host status text and
/// aggregate health, built independently and concurrently with this epic) and the window/tray
/// this epic builds. Neither the window nor the tray depends on how rows or aggregate health are
/// derived, only on this shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>What ships in this delivery.</b> <see cref="Placeholder.PlaceholderStatusReadModel"/> is a
/// deliberately minimal implementation -- it derives rows and health directly from
/// <c>IMountSupervisor.GetSnapshot()</c> with simple, NON-causal status text (e.g. "Unreachable"
/// rather than "unreachable -- 4 consecutive probe failures over the last 3 minutes"). It exists
/// only so the window and tray are functional and testable end to end while bs-ww9.6 is built
/// concurrently in a separate worktree. It is explicitly NOT the deliverable bs-ww9.6 owes --
/// see that class's own remarks for exactly what it does and does not attempt.
/// </para>
/// <para>
/// <b>The plan to replace it (per this project's rule against unresolved "temporary" code).</b>
/// Once bs-ww9.6 lands, the composition root (<c>App.xaml.cs</c>) swaps the single line that
/// constructs <see cref="Placeholder.PlaceholderStatusReadModel"/> for whatever concrete type
/// bs-ww9.6 produces -- both implement this same interface, so nothing in
/// <c>Bosun.UI.Tray.TrayIconController</c> or <c>Bosun.UI.MainWindow</c> needs to change.
/// <see cref="Placeholder.PlaceholderStatusReadModel"/> should then be deleted; it is not meant to
/// survive alongside the real implementation.
/// </para>
/// </remarks>
public interface IStatusReadModel
{
    /// <summary>One row per configured host, in a stable order. Implementations are expected to
    /// be cheap to call repeatedly (the UI polls this on a timer -- the supervisor itself exposes
    /// no change event, by design; see <c>IMountSupervisor</c>'s remarks) -- typically backed by
    /// an internally-cached snapshot rather than doing real work on every call.</summary>
    IReadOnlyList<HostStatusRow> GetRows();

    /// <summary>The single value the tray icon renders (bs-ww9.5's own binding, via
    /// <see cref="TrayIconAppearanceSelector"/>) -- computed from the same underlying data as
    /// <see cref="GetRows"/>, so the icon and the window can never disagree.</summary>
    AggregateHealth GetAggregateHealth();
}
