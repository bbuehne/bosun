using System.Text.RegularExpressions;

namespace Bosun.Rclone;

/// <summary>
/// Deterministic mapping from a configured host's stable identity
/// (<see cref="Configuration.HostConfig.Key"/>) to the name of the rclone remote Bosun manages
/// for it (bs-e26). One remote per host, named by key -- this is the assumption E4's
/// <c>IRemoteRootLister</c> interface doc already stated ("the real implementation is expected
/// to translate <c>hostKey</c> into whatever remote name E3 derives it as"); this class is where
/// that translation actually lives, and confirms the assumption holds (see the E3 delivery
/// report for the one caveat: this class does not itself reject an illegal name; that is
/// <see cref="Configuration.ConfigValidator"/>'s job via <see cref="IsLegalRemoteName"/>, bs-3ip).
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
    /// Does not itself check that the result is a legal rclone remote name; that is
    /// <see cref="IsLegalRemoteName"/>, enforced up front by
    /// <see cref="Configuration.ConfigValidator"/> (rule <c>invalid-rclone-remote-name</c>,
    /// bs-3ip) so a bad host key is a locatable config error rather than an rc-level failure at
    /// provisioning or mount time. TOML bare keys (<c>[A-Za-z0-9_-]</c>) are always legal once
    /// prefixed; a quoted TOML key (<c>[hosts."my:host"]</c>) can be arbitrary text, which is
    /// what the validator guards against.
    /// </remarks>
    public static string RemoteNameFor(string hostKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostKey);
        return Prefix + hostKey;
    }

    /// <summary>
    /// rclone's config-name grammar, ported verbatim from
    /// <c>configNameRe</c> in rclone's <c>fs/fspath/path.go</c>
    /// (https://github.com/rclone/rclone/blob/master/fs/fspath/path.go, last changed at
    /// 3c596f8d111f6c38656e0fb26d30a40da6b0ab80; read against rclone v1.75.2):
    /// <code>
    /// configNameRe = `[\w\p{L}\p{N}.+@]+(?:[ -]+[\w\p{L}\p{N}.+@-]+)*`
    /// // May contain Unicode numbers and letters, as well as `_` (covered by \w), `-`, `.`, `+`,
    /// // `@` and space, but not start with `-` (it complicates usage, see #4261) or space, and
    /// // not end with space
    /// </code>
    /// <c>fspath.CheckConfigName</c> accepts a name iff it matches <c>^configNameRe$</c>, and the
    /// remote-name parser (<c>remoteNameMatcher</c>) uses the same <c>configNameRe</c>. Go's
    /// <c>\w</c> is ASCII-only (<c>[0-9A-Za-z_]</c>), unlike .NET's, so it is spelled out here;
    /// <c>\p{L}</c>/<c>\p{N}</c> carry Unicode-version drift between Go and .NET, which can only
    /// matter for exotic characters newly assigned in one but not the other.
    /// </summary>
    /// <remarks>
    /// Anchored with <c>\A</c>/<c>\z</c>, not <c>^</c>/<c>$</c>: .NET's <c>$</c> also matches
    /// before a trailing newline, which rclone's would not (see the same note on
    /// <see cref="Configuration.ConfigValidator"/>'s drive-letter pattern).
    /// </remarks>
    private static readonly Regex ConfigNamePattern = new(
        @"\A[0-9A-Za-z_\p{L}\p{N}.+@]+(?:[ \-]+[0-9A-Za-z_\p{L}\p{N}.+@\-]+)*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="remoteName"/> is a name rclone's <c>fspath.CheckConfigName</c>
    /// would accept. Use on the output of <see cref="RemoteNameFor"/>.
    /// </summary>
    public static bool IsLegalRemoteName(string remoteName)
    {
        ArgumentNullException.ThrowIfNull(remoteName);
        return ConfigNamePattern.IsMatch(remoteName);
    }

    /// <summary>
    /// rclone's own wording for the rule (<c>errInvalidCharacters</c> in the same file), for
    /// inclusion in validation messages.
    /// </summary>
    public const string RemoteNameRuleDescription =
        "may only contain numbers, letters, `_`, `-`, `.`, `+`, `@` and space, while not start with `-` or space, and not end with space";

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
