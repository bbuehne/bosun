namespace Bosun.Supervisor;

/// <summary>
/// Thrown by <see cref="IMountSupervisor.RepairAllAsync"/> when "Unmount all and re-probe" cannot be
/// honoured, so the caller can say so instead of the click appearing to succeed (bs-mhs). Today the one
/// reason is that the system is suspended (Invariant I8 in reverse: a probe or re-enable in the moments
/// around a suspend is what the invariant forbids), and nothing was changed.
/// </summary>
/// <remarks>
/// The repair counterpart of <see cref="MountRequestRefusedException"/> (bs-oly), with the same shape and
/// the same rule: a click is never a silent no-op. It is separate because that type's message is about
/// mounting one named host ("Cannot mount 'x': ..."), which is wrong for a command that is not about a
/// host or a mount.
/// </remarks>
public sealed class RepairRefusedException(string reason)
    : InvalidOperationException($"Cannot unmount all and re-probe: {reason}")
{
    /// <summary>Why the repair was refused, written to be shown ("the system is suspended").</summary>
    public string Reason { get; } = reason;
}
