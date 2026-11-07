namespace Bosun.Supervisor;

/// <summary>
/// Thrown by <see cref="IMountSupervisor.RepairAllAsync"/> when "Unmount all and re-probe" started and was
/// then cut short because a suspend became pending (bs-5ga.2, ADR-023 amendment bs-5ga.1), so the caller
/// can say so instead of the click reporting success for a repair that only partly ran.
/// </summary>
/// <remarks>
/// The sibling of <see cref="RepairRefusedException"/>, not a subclass of it, because the two differ in
/// the one fact a message must get right: a refused repair did nothing ("Nothing was unmounted"), a
/// stopped repair did part of its work and that work stays done. The distinction is kept in the log too:
/// "refused" is before the first host, "stopped" is partway.
/// <para>Throwing changes only what the caller is told. The hosts the repair had not reached are left
/// exactly as they were for the suspend, which drains them as <see cref="DrainCause.Suspend"/> (an on-demand
/// one gets its ADR-022 remount record and returns at wake); no counter or backoff is touched.</para>
/// </remarks>
public sealed class RepairStoppedException(string reason)
    : InvalidOperationException($"Unmount all and re-probe stopped: {reason}")
{
    /// <summary>Why the repair stopped, written to be shown ("the computer is going to sleep").</summary>
    public string Reason { get; } = reason;
}
