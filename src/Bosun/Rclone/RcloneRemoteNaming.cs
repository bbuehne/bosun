namespace Bosun.Rclone;

/// <summary>
/// Deterministic mapping from a configured host's stable identity
/// (<see cref="Configuration.HostConfig.Key"/>) to the name of the rclone remote Bosun manages
/// for it (bs-e26). One remote per host, named by key -- this is the assumption E4's
/// <c>IRemoteRootLister</c> interface doc already stated ("the real implementation is expected
/// to translate <c>hostKey</c> into whatever remote name E3 derives it as"); this class is where
/// that translation actually lives, and confirms the assumption holds (see the E3 delivery
/// report for the one caveat: host keys are not independently re-validated against rclone's own
/// remote-name character rules here -- see <see cref="RemoteNameFor"/> remarks).
/// </summary>
public static class RcloneRemoteNaming
{
    /// <summary>
    /// Prefix applied to every host key to form the remote name. Never bare
    /// <c>host.Key</c> alone: a prefix keeps Bosun's remotes visibly distinct from any remote the
    /// user already has in their own <c>rclone.conf</c>, so <see cref="RcloneClient.CreateConfigAsync"/>
    /// (an authoritative overwrite of the named section) can never collide with -- and silently
    /// clobber -- a remote the user created by hand under the same short name (bs-e26's "don't
    /// clobber unrelated user remotes").
    /// </summary>
    private const string Prefix = "bosun-";

    /// <summary>
    /// The rclone remote name for <paramref name="hostKey"/>: <c>bosun-&lt;hostKey&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Assumes <paramref name="hostKey"/> is already a valid rclone remote-name component.
    /// TOML bare keys (what <c>[hosts.&lt;key&gt;]</c> requires -- see
    /// <see cref="Configuration.HostConfig.Key"/>) allow the same
    /// <c>[A-Za-z0-9_-]</c> charset that rclone remote names accept in the common case, but
    /// rclone additionally rejects a name starting with certain characters or containing
    /// reserved sequences; this is not independently re-validated here. A host key that is a
    /// valid TOML bare key but an invalid rclone remote name would surface as an rc-level error
    /// from <see cref="RcloneClient.CreateConfigAsync"/>, not a silent misconfiguration -- but it
    /// is not caught earlier, at config-validation time. Flagged as discovered work; see the E3
    /// delivery report.
    /// </remarks>
    public static string RemoteNameFor(string hostKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostKey);
        return Prefix + hostKey;
    }

    /// <summary>
    /// The rclone fs spec for mounting/listing <paramref name="remotePath"/> on the remote
    /// belonging to <paramref name="hostKey"/>, e.g. <c>bosun-example-nas:/volume1/share</c>.
    /// </summary>
    public static string RemoteFsPath(string hostKey, string remotePath)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        return $"{RemoteNameFor(hostKey)}:{remotePath}";
    }
}
