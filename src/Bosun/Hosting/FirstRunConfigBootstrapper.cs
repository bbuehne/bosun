using System.IO;
using Bosun.Configuration;

namespace Bosun.Hosting;

/// <summary>Real, filesystem-backed <see cref="IFirstRunConfigBootstrapper"/> (bs-008). Production
/// only -- tests always inject a fake pointed at a temp directory.</summary>
public sealed class FirstRunConfigBootstrapper : IFirstRunConfigBootstrapper
{
    public bool EnsureConfigExists(string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{configPath}' has no containing directory.", nameof(configPath));

        // Unconditional, even when the file already exists: this is the guarantee that lets
        // StartupOrchestrator resolve IHostConfigStore (and therefore construct
        // FileSystemConfigWatcher, whose constructor throws if its directory is missing)
        // immediately afterward without a separate existence check of its own.
        Directory.CreateDirectory(directory);

        if (File.Exists(fullPath))
        {
            return false;
        }

        File.WriteAllText(fullPath, FirstRunConfigTemplate.Content);
        return true;
    }
}

/// <summary>
/// The template <see cref="FirstRunConfigBootstrapper"/> writes to a fresh
/// <c>%LOCALAPPDATA%\Bosun\hosts.toml</c> (ADR-012 Decision 4, bs-008). The <c>[global]</c> block
/// is built from <see cref="GlobalConfig"/>'s own default constants, not hand-copied literals, so
/// this cannot silently drift from the values <c>ConfigParser</c> would apply anyway if the block
/// were absent entirely. The example-host guidance below is a hand-maintained copy of
/// config/hosts.example.toml with every host block commented out -- ADR-012 Decision 2 is
/// explicit that shipping a template which tries to mount <c>nas.example.internal</c> on first
/// launch is worse than no config at all, so nothing here is active. Keep this in sync with
/// config/hosts.example.toml when that file's shape changes.
/// </summary>
internal static class FirstRunConfigTemplate
{
    public static string Content { get; } = Build();

    private static string Build()
    {
        var backoffSeconds = string.Join(", ", GlobalConfig.DefaultBackoffSeconds);
        var rcloneConfigPath = GlobalConfig.DefaultRcloneConfigPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        var startWithWindows = GlobalConfig.DefaultStartWithWindows ? "true" : "false";

        return $$"""
            # Bosun host configuration
            #
            # Created automatically on first run. Configures zero hosts -- Bosun will not attempt
            # to reach or mount anything until you add a [hosts.<key>] section below and restart
            # Bosun. Full schema: docs/CONFIG-SCHEMA.md. This file is yours to edit; Bosun only
            # writes it once, the first time it finds nothing here.
            #
            # An annotated, fuller example lives at config/hosts.example.toml in the Bosun
            # repository. The commented-out block below is a copy of one entry from it, to get you
            # started -- uncomment and edit it (hostname, user, identity_file, drive, remote_path
            # at minimum), or write your own from scratch.

            [global]
            rclone_rc_port                  = {{GlobalConfig.DefaultRcloneRcPort}}
            rclone_config_path              = "{{rcloneConfigPath}}"
            probe_timeout_seconds           = {{GlobalConfig.DefaultProbeTimeoutSeconds}}
            failures_before_unmount         = {{GlobalConfig.DefaultFailuresBeforeUnmount}}
            backoff_seconds                 = [{{backoffSeconds}}]
            mounted_probe_interval_seconds  = {{GlobalConfig.DefaultMountedProbeIntervalSeconds}}
            suspend_unmount_timeout_seconds = {{GlobalConfig.DefaultSuspendUnmountTimeoutSeconds}}
            start_with_windows              = {{startWithWindows}}


            # An always-on box on the LAN. Persistent mount, quiet session.
            # [hosts.example-nas]
            # display_name  = "Example NAS"
            # hostname      = "nas.example.internal"
            # port          = 22
            # user          = "youruser"
            # identity_file = "~/.ssh/id_ed25519"
            #
            #   [hosts.example-nas.mount]
            #   mode                 = "persistent"
            #   drive                = "N:"
            #   remote_path          = "/volume1/share"
            #   vfs_cache_mode       = "writes"
            #   network_mode         = true
            #   idle_unmount_seconds = 0
            #
            #   [hosts.example-nas.session]
            #   autostart    = false
            #   reconnect    = true
            #   tmux         = true
            #   tmux_session = "main"
            #   tab_color    = "#2D5F3F"
            #   color_scheme = "Campbell"
            #
            #   [hosts.example-nas.probe]
            #   interval_seconds = 60
            #   deep_probe       = true
            """;
    }
}
