namespace Bosun.Probe;

/// <summary>
/// The one rclone-facing operation the deep probe needs: list the configured remote's root,
/// depth 1, via <c>operations/list</c> (docs/ARCHITECTURE.md §3, §"IProbe"). Defined here, in E4,
/// as an interface only — E3 owns <c>IRcloneClient</c> and will provide the real implementation
/// once it lands (docs/WORK-BREAKDOWN.md deliberately makes E4 depend on E2 only, not E3; E4 must
/// not block on E3 landing first).
/// </summary>
/// <remarks>
/// Deliberately narrow: nothing about rclone's <c>config/*</c>, <c>mount/*</c>, or
/// <c>core/version</c> endpoints belongs here, only the single call
/// <see cref="HostProbe.ProbeDeepAsync"/> actually needs. A wider seam would produce a wider fake
/// and a false sense of integration coverage — see the epic brief's explicit warning about this.
/// The real implementation is expected to translate <c>hostKey</c> into whatever remote name E3
/// derives it as (docs/WORK-BREAKDOWN.md E3: "Ensure an sftp remote exists per configured host,
/// derived from config") — that mapping is E3's concern, not this interface's.
/// </remarks>
public interface IRemoteRootLister
{
    /// <summary>
    /// Lists the root of the rclone remote associated with <paramref name="hostKey"/>
    /// (<see cref="Configuration.HostConfig.Key"/>), depth 1. Completes on success; throws on any
    /// failure (auth, transport, an error rclone reports in the response) so
    /// <see cref="HostProbe"/> can classify it the same way it classifies shallow-probe failures.
    /// Deliberately returns nothing on success — the deep probe only needs to know that the call
    /// succeeded, never what the remote's root actually contains.
    /// </summary>
    Task ListRootAsync(string hostKey, CancellationToken cancellationToken);
}
