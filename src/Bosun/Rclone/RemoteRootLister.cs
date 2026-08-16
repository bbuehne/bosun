using Bosun.Configuration;
using Bosun.Probe;

namespace Bosun.Rclone;

/// <summary>
/// Real <see cref="IRemoteRootLister"/> (E4's seam, implemented here in E3 as promised by that
/// interface's own doc comment). Lists the ROOT of the host's rclone remote -- not
/// <c>host.Mount.RemotePath</c> -- via <c>operations/list</c>, depth 1. Deliberately the
/// account root rather than the configured mount subdirectory: the deep probe exists to prove
/// "authentication and the SFTP subsystem work" (docs/ARCHITECTURE.md §3), which is meaningful
/// even for a <c>mount.mode = "none"</c> host that has no <c>remote_path</c> at all (e.g.
/// <c>hosts.example-jump</c> in config/hosts.example.toml).
/// </summary>
/// <remarks>
/// Assumes <see cref="RcloneRemoteProvisioner"/> has already provisioned the remote for this
/// host. If it has not, <c>operations/list</c> fails with an rc-level error, which surfaces to
/// <see cref="HostProbe"/> as a normal <see cref="DeepProbeOutcome.Failed"/> -- not a crash, just
/// a failed probe, which is the correct degrade (the host stays in <c>Probing</c>/<c>Unreachable</c>
/// until provisioning completes). Deciding when provisioning runs relative to probing is
/// orchestration left to whichever epic wires the app together -- see the remarks on
/// <see cref="IRcloneRemoteProvisioner"/>.
/// </remarks>
public sealed class RemoteRootLister(IRcloneClient client, IHostConfigStore configStore) : IRemoteRootLister
{
    public async Task ListRootAsync(string hostKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostKey);

        if (!configStore.Current.Hosts.ContainsKey(hostKey))
        {
            throw new InvalidOperationException($"Unknown host key '{hostKey}' -- not present in the current config.");
        }

        var remoteName = RcloneRemoteNaming.RemoteNameFor(hostKey);

        // fs = "<remote>:" with remote = "" lists the account root, depth 1 (opt.recurse
        // omitted -- see RcloneClient.ListAsync). The result is discarded: the deep probe only
        // needs to know the call succeeded (see IRemoteRootLister.ListRootAsync's own remarks).
        await client.ListAsync($"{remoteName}:", remote: "", cancellationToken).ConfigureAwait(false);
    }
}
