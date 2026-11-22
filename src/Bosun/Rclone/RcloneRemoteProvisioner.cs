using System.Globalization;
using Bosun.Configuration;
using Microsoft.Extensions.Logging;

namespace Bosun.Rclone;

/// <summary>
/// Real <see cref="IRcloneRemoteProvisioner"/> (bs-e26). Maps a <see cref="HostConfig"/> to
/// rclone's <c>sftp</c> backend parameters and ensures the remote named by
/// <see cref="RcloneRemoteNaming.RemoteNameFor"/> matches. Key-based authentication only
/// (Invariant I10, ADR-007) -- no password field is ever set, and <c>ask_password</c> is
/// explicitly forced to <c>false</c> so rclone never falls back to an interactive prompt.
/// Host keys are verified against the user's <c>known_hosts</c> (ADR-025, bs-k9x); without that
/// rclone's sftp backend accepts any key.
/// </summary>
public sealed class RcloneRemoteProvisioner(
    IRcloneClient client,
    IKnownHostsFileLocator knownHostsFile,
    ILogger<RcloneRemoteProvisioner> logger) : IRcloneRemoteProvisioner
{
    /// <summary>rclone's remote type for SFTP. Source: https://rclone.org/sftp/.</summary>
    private const string SftpRemoteType = "sftp";

    public async Task EnsureRemoteAsync(HostConfig host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        var remoteName = RcloneRemoteNaming.RemoteNameFor(host.Key);
        var desired = BuildParameters(host);

        IReadOnlyDictionary<string, string>? current = null;
        try
        {
            current = await client.GetConfigAsync(remoteName, cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneRcException ex)
        {
            // Could not confirm current state -- fall through and (re)assert desired state.
            // config/create is an authoritative overwrite of only this named section, so doing
            // so here is safe even if the "failure" was actually "the remote already exists and
            // is already correct" (RcloneClient.GetConfigAsync collapses both cases -- see its
            // remarks).
            logger.LogDebug(ex, "Could not read current config for remote {RemoteName}; will (re)create it", remoteName);
        }

        if (current is not null && Matches(current, desired))
        {
            return;
        }

        logger.LogInformation("Provisioning rclone remote {RemoteName} for host {HostKey}", remoteName, host.Key);
        await client.CreateConfigAsync(remoteName, SftpRemoteType, desired, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureAllRemotesAsync(BosunConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        foreach (var host in config.Hosts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureRemoteAsync(host, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Maps a host's connection fields to rclone <c>sftp</c> backend parameters. Field names
    /// (<c>host</c>, <c>port</c>, <c>user</c>, <c>key_file</c>, <c>ask_password</c>,
    /// <c>known_hosts_file</c>) verified against https://rclone.org/sftp/ (<c>known_hosts_file</c> is
    /// the config key of <c>--sftp-known-hosts-file</c>, "Optional path to known_hosts file. Set this
    /// value to enable server host key validation"; rclone also checked in
    /// <c>backend/sftp/sftp.go</c>, where a non-empty value other than <c>none</c> installs
    /// <c>knownhosts.New</c> as the host-key callback). <c>identity_file</c> is expanded via
    /// <see cref="ConfigValidator.ExpandHome"/> -- the same expansion
    /// <see cref="ConfigValidator"/> uses for its existence check, so the path handed to rclone
    /// matches the one already validated to exist. <c>known_hosts_file</c> comes from
    /// <see cref="IKnownHostsFileLocator"/> as an absolute path (ADR-025 Decision 1).
    /// </summary>
    private Dictionary<string, string> BuildParameters(HostConfig host) => new(StringComparer.Ordinal)
    {
        ["host"] = host.Hostname,
        ["port"] = host.Port.ToString(CultureInfo.InvariantCulture),
        ["user"] = host.User,
        ["key_file"] = ConfigValidator.ExpandHome(host.IdentityFile),

        // ADR-025: verify the server's key against the file ssh.exe uses. Bosun never writes to it.
        // Part of the desired state, so a remote provisioned before this existed no longer
        // Matches and is re-provisioned on the next start.
        ["known_hosts_file"] = knownHostsFile.Path,

        // Invariant I10 / ADR-007: key-based auth only. No "pass" is ever set, and this
        // explicitly forbids rclone falling back to an interactive password prompt.
        ["ask_password"] = "false",
    };

    private static bool Matches(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> desired)
    {
        foreach (var (key, value) in desired)
        {
            if (!current.TryGetValue(key, out var currentValue) || !string.Equals(currentValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
