using Bosun.Configuration;

namespace Bosun.Rclone;

/// <summary>
/// Ensures an rclone remote exists (and is correctly configured) for a configured host (bs-e26,
/// WORK-BREAKDOWN E3: "Ensure an sftp remote exists per configured host, derived from config").
/// Provisioning runs for every host, not only mounting ones -- <see cref="Probe.HostProbe"/>'s
/// deep probe needs a remote too, and a host can have <c>deep_probe = true</c> while
/// <c>mount.mode = "none"</c>.
/// </summary>
/// <remarks>
/// <b>Not wired into any startup path yet.</b> E3 builds and tests this in isolation; deciding
/// WHEN it runs (once at startup before probing begins? again on every config reload? on rcd
/// restart, per docs/ARCHITECTURE.md §6's "rclone rcd dies" row?) is bootstrap/orchestration
/// logic that belongs to whichever epic wires the whole app together (E5's supervisor bootstrap
/// or E7). Calling <see cref="EnsureAllRemotesAsync"/> before probing starts, and again whenever
/// <c>IHostConfigStore.ConfigChanged</c> fires, is the obvious shape, but it is not this epic's
/// place to guess at that orchestration -- see CLAUDE.md's "ask before inventing behaviour".
/// </remarks>
public interface IRcloneRemoteProvisioner
{
    /// <summary>
    /// Ensures the rclone remote for <paramref name="host"/> exists with the correct
    /// <c>host</c>/<c>port</c>/<c>user</c>/<c>key_file</c> parameters, creating or correcting it
    /// if necessary. Idempotent and safe to call repeatedly.
    /// </summary>
    Task EnsureRemoteAsync(HostConfig host, CancellationToken cancellationToken);

    /// <summary>Calls <see cref="EnsureRemoteAsync"/> for every host in <paramref name="config"/>.</summary>
    Task EnsureAllRemotesAsync(BosunConfig config, CancellationToken cancellationToken);
}
