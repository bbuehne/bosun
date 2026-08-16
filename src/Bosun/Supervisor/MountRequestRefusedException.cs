namespace Bosun.Supervisor;

/// <summary>
/// Thrown by <see cref="IMountSupervisor.RequestMountAsync"/> when an explicit mount request
/// cannot be honoured for a reason specific to THIS host or to system state -- never a silent
/// no-op (ADR-014 rule 8: "it is never a silent no-op -- a tray menu item that does nothing,
/// forever, with no error is not a defensible behaviour"). Two situations throw this:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The host was <see cref="MountState.Unreachable"/>; rule 8's immediate probe was issued and
/// came back negative, so the host is still unreachable and there is nothing to mount.</item>
/// <item>The system is currently suspended (Invariant I8): triggering a probe -- let alone a mount
/// -- in the moments around a suspend would be I8 in reverse, so the request is refused outright
/// rather than attempted.</item>
/// </list>
/// <para>
/// Deliberately distinct from <see cref="MountingUnavailableException"/>, which reports a
/// PROCESS-WIDE reason (WinFsp missing, rclone unhealthy) unrelated to this particular host's own
/// reachability. Both follow the same shape -- host key plus a causal <see cref="Reason"/> string
/// -- so a caller (the tray) can render either the same way: "&lt;host&gt;: &lt;reason&gt;".
/// </para>
/// </remarks>
public sealed class MountRequestRefusedException(string hostKey, string reason)
    : InvalidOperationException($"Cannot mount '{hostKey}': {reason}")
{
    public string HostKey { get; } = hostKey;

    public string Reason { get; } = reason;
}
