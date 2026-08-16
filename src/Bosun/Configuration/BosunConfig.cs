namespace Bosun.Configuration;

/// <summary>
/// The fully bound, immutable configuration read from <c>config/hosts.toml</c>
/// (docs/CONFIG-SCHEMA.md). Produced by <see cref="ConfigParser"/> and, once
/// validated by <see cref="ConfigValidator"/>, served by <see cref="IHostConfigStore"/>.
/// </summary>
/// <remarks>
/// This is a bind-only artifact (bs-0na): every field here is exactly what was written in
/// the TOML, with <c>[global]</c> defaults applied where documented. No cross-field or
/// cross-host validation (drive collisions, missing files, etc.) has happened yet — that
/// is <see cref="ConfigValidator"/> (bs-c0g).
/// </remarks>
public sealed record BosunConfig
{
    public required GlobalConfig Global { get; init; }

    /// <summary>
    /// Keyed by the host's stable TOML identity (the <c>[hosts.&lt;key&gt;]</c> key itself,
    /// e.g. <c>example-nas</c>) — not by <see cref="HostConfig.DisplayName"/>, which is
    /// presentation-only and may change without affecting identity. See bs-08g.
    /// </summary>
    public required IReadOnlyDictionary<string, HostConfig> Hosts { get; init; }
}

/// <summary>The <c>[global]</c> block. Every field is optional in TOML; absence means the
/// documented default (docs/CONFIG-SCHEMA.md "Global"), applied here at bind time.</summary>
public sealed record GlobalConfig
{
    public const int DefaultRcloneRcPort = 5572;
    public const string DefaultRcloneConfigPath = @"%APPDATA%\rclone\rclone.conf";
    public const int DefaultProbeTimeoutSeconds = 5;
    public const int DefaultFailuresBeforeUnmount = 3;
    public const int DefaultMountedProbeIntervalSeconds = 60; // ADR-011
    public const int DefaultSuspendUnmountTimeoutSeconds = 8;
    public const bool DefaultStartWithWindows = true;

    public static readonly IReadOnlyList<int> DefaultBackoffSeconds = [5, 15, 30, 60, 300];

    public int RcloneRcPort { get; init; } = DefaultRcloneRcPort;
    public string RcloneConfigPath { get; init; } = DefaultRcloneConfigPath;
    public int ProbeTimeoutSeconds { get; init; } = DefaultProbeTimeoutSeconds;
    public int FailuresBeforeUnmount { get; init; } = DefaultFailuresBeforeUnmount;
    public IReadOnlyList<int> BackoffSeconds { get; init; } = DefaultBackoffSeconds;

    /// <summary>Upper bound on the probe interval for a host in <c>Mounted</c>. Absent in
    /// TOML is NOT an error — it defaults to 60 (ADR-011). Zero/negative is rejected, but
    /// only by <see cref="ConfigValidator"/>; this type accepts whatever was bound.</summary>
    public int MountedProbeIntervalSeconds { get; init; } = DefaultMountedProbeIntervalSeconds;

    public int SuspendUnmountTimeoutSeconds { get; init; } = DefaultSuspendUnmountTimeoutSeconds;
    public bool StartWithWindows { get; init; } = DefaultStartWithWindows;
}

/// <summary>One <c>[hosts.&lt;key&gt;]</c> entry.</summary>
public sealed record HostConfig
{
    /// <summary>The TOML key — e.g. <c>example-nas</c> in <c>[hosts.example-nas]</c>. This is
    /// the stable identity used throughout the system (drive-letter reconciliation, probe
    /// scheduling, session correlation). Never the same as <see cref="DisplayName"/>, which is
    /// presentation only and may collide in ways that are a validation error, not an identity
    /// collision.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }
    public required string Hostname { get; init; }
    public required int Port { get; init; }
    public required string User { get; init; }

    /// <summary>Literal path as written in TOML (e.g. <c>~/.ssh/id_ed25519</c>) — not expanded.
    /// Expansion and existence-checking happen in <see cref="ConfigValidator"/>.</summary>
    public required string IdentityFile { get; init; }

    public required MountConfig Mount { get; init; }
    public required SessionConfig Session { get; init; }
    public required ProbeConfig Probe { get; init; }
}

public enum MountMode
{
    Persistent,
    OnDemand,
    None,
}

/// <summary>
/// <c>[hosts.&lt;key&gt;.mount]</c>. When <see cref="Mode"/> is <see cref="MountMode.None"/>,
/// every other field is typically absent from TOML (see <c>hosts.example-jump</c> in
/// config/hosts.example.toml) and therefore null/default here — that is legal at bind time;
/// <see cref="ConfigValidator"/> is what enforces "required when mode != none".
///
/// <see cref="VfsCacheMode"/> is the one exception to "absent means null": Invariant I6 makes
/// "writes" a correctness floor for every mount, and rclone's own default is "off" — the exact
/// value I6 forbids — so <see cref="ConfigParser"/> defaults an absent value to
/// <see cref="DefaultVfsCacheMode"/> at bind time (bs-lrd / bs-mb2), unconditionally of
/// <see cref="Mode"/>. Nothing downstream (mount building, this record's consumers) should ever
/// see a null here for a value ConfigParser bound.
/// </summary>
public sealed record MountConfig
{
    public const string DefaultVfsCacheMode = "writes";

    public required MountMode Mode { get; init; }
    public string? Drive { get; init; }
    public string? RemotePath { get; init; }
    public string? VfsCacheMode { get; init; }
    public bool? NetworkMode { get; init; }
    public int? IdleUnmountSeconds { get; init; }
}

/// <summary><c>[hosts.&lt;key&gt;.session]</c>.</summary>
public sealed record SessionConfig
{
    public required bool Autostart { get; init; }
    public required bool Reconnect { get; init; }
    public required bool Tmux { get; init; }

    /// <summary>Only meaningful (and, per the example config, only ever present) when
    /// <see cref="Tmux"/> is true.</summary>
    public string? TmuxSession { get; init; }

    public required string TabColor { get; init; }
    public required string ColorScheme { get; init; }
}

/// <summary><c>[hosts.&lt;key&gt;.probe]</c>.</summary>
public sealed record ProbeConfig
{
    /// <summary>Polling cadence while the host is idle (<c>Ready</c> / <c>Unreachable</c>).
    /// <c>0</c> means never poll while idle (ADR-008's on-demand default). Does not govern
    /// probing while <c>Mounted</c> — see <see cref="GlobalConfig.MountedProbeIntervalSeconds"/>
    /// and ADR-011.</summary>
    public required int IntervalSeconds { get; init; }

    public required bool DeepProbe { get; init; }
}
