namespace Bosun.Probe;

/// <summary>
/// Two-tier host liveness/readiness check (docs/ARCHITECTURE.md §3). Shallow proves TCP
/// reachability only; deep proves authentication and the SFTP subsystem work, via
/// <c>operations/list</c> on the remote root, depth 1.
/// </summary>
/// <remarks>
/// Invariant I1 — "never mount a host that has not passed a probe" — really means "never mount a
/// host that has not passed a <em>deep</em> probe". <see cref="ShallowProbeResult"/> and
/// <see cref="DeepProbeResult"/> are deliberately unrelated types: no shared base, no implicit or
/// explicit conversion between them, no common "IsSuccess" surface a caller could read
/// polymorphically. That is intentional — it makes "authorise a mount from a shallow pass" a
/// compile error, not a code-review finding.
/// </remarks>
public interface IProbe
{
    /// <summary>
    /// TCP-connects to <paramref name="hostname"/>:<paramref name="port"/>, honouring
    /// <paramref name="timeout"/> and <paramref name="cancellationToken"/>. Cheap, and the
    /// recurring liveness check while a host is <c>Ready</c>/<c>Unreachable</c>/<c>Mounted</c>
    /// (docs/ARCHITECTURE.md §3) — but success here proves reachability only, never SFTP
    /// readiness (Invariant I1, see remarks on <see cref="IProbe"/>). Never use this result to
    /// authorise a transition into <c>Mounting</c>; that requires <see cref="ProbeDeepAsync"/>.
    /// </summary>
    Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Proves authentication and the SFTP subsystem work for the host identified by
    /// <paramref name="hostKey"/> (<see cref="Configuration.HostConfig.Key"/>), via
    /// <c>operations/list</c> on the remote root, depth 1, honouring <paramref name="timeout"/>
    /// and <paramref name="cancellationToken"/>. Run once before every transition into
    /// <c>Mounting</c> (docs/ARCHITECTURE.md §4 rule 2) — this, not
    /// <see cref="ProbeShallowAsync"/>, is what authorises a mount.
    /// </summary>
    Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Outcome of a shallow (TCP-only) probe. Never sufficient to authorise a mount — see
/// the remarks on <see cref="IProbe"/> and Invariant I1.</summary>
public enum ShallowProbeOutcome
{
    Success,
    Timeout,
    ConnectionRefused,
    DnsFailure,
    Cancelled,

    /// <summary>Any other socket-level failure not distinguished above (e.g. network/host
    /// unreachable, connection reset). Kept distinct from the categories above rather than
    /// folded into one of them, so a log line never mislabels an uncommon failure as a common
    /// one — see the brief's requirement that "why a probe failed has to survive into the
    /// log".</summary>
    OtherFailure,
}

/// <summary>
/// Result of a shallow probe. E5 logs <see cref="Outcome"/> and <see cref="Detail"/> as the
/// trigger for every state transition it causes (docs/ARCHITECTURE.md §5).
/// </summary>
public sealed record ShallowProbeResult
{
    public required ShallowProbeOutcome Outcome { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>Underlying exception message when <see cref="Outcome"/> is not
    /// <see cref="ShallowProbeOutcome.Success"/>; <see langword="null"/> on success.</summary>
    public string? Detail { get; init; }
}

/// <summary>Outcome of a deep (<c>operations/list</c>) probe. <see cref="Success"/> is the only
/// signal that ever authorises a transition into <c>Mounting</c> (Invariant I1).</summary>
public enum DeepProbeOutcome
{
    Success,
    Timeout,
    Cancelled,

    /// <summary>Anything else the remote-root listing failed with: auth failure, SFTP subsystem
    /// unavailable, transport/rclone error. See <see cref="DeepProbeResult.Detail"/> for the
    /// specific reason to log.</summary>
    Failed,
}

/// <summary>Result of a deep probe.</summary>
public sealed record DeepProbeResult
{
    public required DeepProbeOutcome Outcome { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>Underlying error message when <see cref="Outcome"/> is not
    /// <see cref="DeepProbeOutcome.Success"/>; <see langword="null"/> on success.</summary>
    public string? Detail { get; init; }
}
