using Tomlyn.Serialization;

namespace Bosun.Configuration;

// Mutable, fully-nullable POCOs used only as the Tomlyn deserialization target. Kept internal
// and separate from the public immutable model (BosunConfig.cs) so that:
//   - "field absent from TOML" is representable (null) distinctly from "field present with a
//     falsy/zero value", which the mapping in ConfigParser needs to tell apart (e.g. an
//     explicit `backoff_seconds = []` must be preserved as empty for the validator to reject,
//     not silently replaced by the default ladder).
//   - the public model can stay `required`/non-nullable where the schema says a field always
//     exists, without fighting the deserializer over exactly how it constructs objects.

internal sealed class BosunConfigRaw
{
    [TomlPropertyName("global")]
    public GlobalConfigRaw? Global { get; set; }

    [TomlPropertyName("hosts")]
    public Dictionary<string, HostConfigRaw>? Hosts { get; set; }
}

internal sealed class GlobalConfigRaw
{
    [TomlPropertyName("rclone_rc_port")]
    public int? RcloneRcPort { get; set; }

    [TomlPropertyName("rclone_config_path")]
    public string? RcloneConfigPath { get; set; }

    [TomlPropertyName("probe_timeout_seconds")]
    public int? ProbeTimeoutSeconds { get; set; }

    [TomlPropertyName("failures_before_unmount")]
    public int? FailuresBeforeUnmount { get; set; }

    [TomlPropertyName("backoff_seconds")]
    public List<int>? BackoffSeconds { get; set; }

    [TomlPropertyName("mounted_probe_interval_seconds")]
    public int? MountedProbeIntervalSeconds { get; set; }

    [TomlPropertyName("mounted_deep_probe_interval_seconds")]
    public int? MountedDeepProbeIntervalSeconds { get; set; }

    [TomlPropertyName("suspend_unmount_timeout_seconds")]
    public int? SuspendUnmountTimeoutSeconds { get; set; }

    [TomlPropertyName("start_with_windows")]
    public bool? StartWithWindows { get; set; }
}

internal sealed class HostConfigRaw
{
    [TomlPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [TomlPropertyName("hostname")]
    public string? Hostname { get; set; }

    [TomlPropertyName("port")]
    public int? Port { get; set; }

    [TomlPropertyName("user")]
    public string? User { get; set; }

    [TomlPropertyName("identity_file")]
    public string? IdentityFile { get; set; }

    [TomlPropertyName("mount")]
    public MountConfigRaw? Mount { get; set; }

    [TomlPropertyName("session")]
    public SessionConfigRaw? Session { get; set; }

    [TomlPropertyName("probe")]
    public ProbeConfigRaw? Probe { get; set; }
}

internal sealed class MountConfigRaw
{
    [TomlPropertyName("mode")]
    public string? Mode { get; set; }

    [TomlPropertyName("drive")]
    public string? Drive { get; set; }

    [TomlPropertyName("remote_path")]
    public string? RemotePath { get; set; }

    [TomlPropertyName("vfs_cache_mode")]
    public string? VfsCacheMode { get; set; }

    [TomlPropertyName("network_mode")]
    public bool? NetworkMode { get; set; }

    [TomlPropertyName("idle_unmount_seconds")]
    public int? IdleUnmountSeconds { get; set; }
}

internal sealed class SessionConfigRaw
{
    [TomlPropertyName("autostart")]
    public bool? Autostart { get; set; }

    [TomlPropertyName("reconnect")]
    public bool? Reconnect { get; set; }

    [TomlPropertyName("tmux")]
    public bool? Tmux { get; set; }

    [TomlPropertyName("tmux_session")]
    public string? TmuxSession { get; set; }

    [TomlPropertyName("tab_color")]
    public string? TabColor { get; set; }

    [TomlPropertyName("color_scheme")]
    public string? ColorScheme { get; set; }
}

internal sealed class ProbeConfigRaw
{
    [TomlPropertyName("interval_seconds")]
    public int? IntervalSeconds { get; set; }

    [TomlPropertyName("deep_probe")]
    public bool? DeepProbe { get; set; }
}
