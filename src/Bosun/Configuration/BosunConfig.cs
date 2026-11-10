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

    /// <summary>The top-level <c>[claude_sessions]</c> table (ADR-021 Decision 11). Not per host.
    /// Absent table and absent field both mean disabled; deliberately not <c>required</c> so
    /// every existing construction site keeps meaning "feature off".</summary>
    public ClaudeSessionsConfig ClaudeSessions { get; init; } = new();

    /// <summary>The top-level <c>[remote_sessions]</c> table (ADR-024, bs-4xw.6). Not per host.
    /// Absent table and absent field both mean disabled; deliberately not <c>required</c> so every
    /// existing construction site keeps meaning "feature off". Nothing consumes it yet.</summary>
    public RemoteSessionsConfig RemoteSessions { get; init; } = new();

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
    public const int DefaultMountedDeepProbeIntervalSeconds = 300; // ADR-016 (bs-2eg)
    public const int DefaultSuspendUnmountTimeoutSeconds = 3; // bs-ajh: PBT_APMSUSPEND allows ~2 s; see docs/ARCHITECTURE.md §3
    public const bool DefaultStartWithWindows = true;

    public static readonly IReadOnlyList<int> DefaultBackoffSeconds = [5, 15, 30, 60, 300];

    public int RcloneRcPort { get; init; } = DefaultRcloneRcPort;
    /// <summary>The path exactly as written in TOML (or the default), unexpanded: it may contain
    /// <c>%VAR%</c> tokens such as <c>%APPDATA%</c>. Kept literal so a config rewrite
    /// (<c>HostConfigTomlWriter</c>) round-trips what the user wrote instead of baking in this
    /// machine's expansion. Consumers wanting a usable file path call
    /// <see cref="ResolveRcloneConfigPath"/> -- the one place environment variables in this
    /// field are expanded (bs-3ip).</summary>
    public string RcloneConfigPath { get; init; } = DefaultRcloneConfigPath;

    /// <summary><see cref="RcloneConfigPath"/> with <c>%VAR%</c> environment-variable tokens
    /// expanded, ready to hand to rclone's <c>--config</c>. The single point of environment
    /// expansion for this field; do not call <see cref="Environment.ExpandEnvironmentVariables(string)"/>
    /// on it elsewhere.</summary>
    /// <param name="expandEnvironmentVariables">Injected expander; defaults to
    /// <see cref="Environment.ExpandEnvironmentVariables(string)"/>. Tests pass a fake so no result
    /// depends on the machine's real environment.</param>
    public string ResolveRcloneConfigPath(Func<string, string>? expandEnvironmentVariables = null) =>
        (expandEnvironmentVariables ?? Environment.ExpandEnvironmentVariables)(RcloneConfigPath);
    public int ProbeTimeoutSeconds { get; init; } = DefaultProbeTimeoutSeconds;
    public int FailuresBeforeUnmount { get; init; } = DefaultFailuresBeforeUnmount;
    public IReadOnlyList<int> BackoffSeconds { get; init; } = DefaultBackoffSeconds;

    /// <summary>Upper bound on the probe interval for a host in <c>Mounted</c>. Absent in
    /// TOML is NOT an error — it defaults to 60 (ADR-011). Zero/negative is rejected, but
    /// only by <see cref="ConfigValidator"/>; this type accepts whatever was bound.</summary>
    public int MountedProbeIntervalSeconds { get; init; } = DefaultMountedProbeIntervalSeconds;

    /// <summary>Cadence of the periodic DEEP probe (<c>operations/list</c>, via
    /// <see cref="Probe.IProbe.ProbeDeepAsync"/>) run for every host in <c>Mounted</c>, on top of
    /// (never instead of) the shallow probe governed by <see cref="MountedProbeIntervalSeconds"/>
    /// (bs-2eg / ADR-016). A shallow TCP probe proves only that the host's port answers — it
    /// cannot detect a mount whose underlying SSH channel died while the TCP-level host stayed
    /// reachable (a sleep/resume cycle, a brief network blip that outlives the SSH session but not
    /// the shallow-probe failure threshold). Deliberately slower than the shallow cadence: it is
    /// bounded by <c>ProbeTimeoutSeconds</c> and goes over loopback HTTP to <c>rclone rcd</c>
    /// (never the mounted drive letter itself — see <c>MountSupervisor</c>'s class remarks on why
    /// enumerating the drive is forbidden), but it is still real traffic against the remote host's
    /// SFTP subsystem and need not run as often as a bare TCP connect. Absent in TOML is NOT an
    /// error — it defaults to 300 (ADR-016), the same "absent means default, not rejected"
    /// treatment ADR-011 established for <see cref="MountedProbeIntervalSeconds"/>. Zero/negative
    /// is rejected by <see cref="ConfigValidator"/>.</summary>
    public int MountedDeepProbeIntervalSeconds { get; init; } = DefaultMountedDeepProbeIntervalSeconds;

    public int SuspendUnmountTimeoutSeconds { get; init; } = DefaultSuspendUnmountTimeoutSeconds;
    public bool StartWithWindows { get; init; } = DefaultStartWithWindows;
}

/// <summary>The <c>[claude_sessions]</c> block (ADR-021 Decision 11, docs/CONFIG-SCHEMA.md "Claude
/// Code sessions").</summary>
public sealed record ClaudeSessionsConfig
{
    public const bool DefaultEnabled = false;

    /// <summary><see langword="true"/> makes Bosun generate its Claude Code plugin, watch session
    /// records, and restore sessions lost to a reboot or Terminal exit. Never on by default: it
    /// leads to a plugin the user installs into their own Claude Code configuration.</summary>
    public bool Enabled { get; init; } = DefaultEnabled;
}

/// <summary>The <c>[remote_sessions]</c> block (ADR-024, docs/CONFIG-SCHEMA.md "Remote sessions").</summary>
public sealed record RemoteSessionsConfig
{
    public const bool DefaultEnabled = false;

    /// <summary><see langword="true"/> lets Bosun start and resume Claude Code sessions on request
    /// from a remote caller. Never on by default.</summary>
    public bool Enabled { get; init; } = DefaultEnabled;

    /// <summary>The development area exactly as written in TOML, unexpanded: it may contain
    /// <c>%VAR%</c> tokens such as <c>%USERPROFILE%</c>. Kept literal so a config rewrite
    /// (<c>HostConfigTomlWriter</c>) round-trips what the user wrote. There is deliberately no
    /// default: when <see cref="Enabled"/> is true a missing value is a validation error rather than
    /// a guessed folder (docs/CONFIG-SCHEMA.md). Consumers wanting a usable path call
    /// <see cref="ResolveDevelopmentArea"/> -- the one place environment variables in this field are
    /// expanded.</summary>
    public string? DevelopmentArea { get; init; }

    /// <summary><see cref="DevelopmentArea"/> with <c>%VAR%</c> environment-variable tokens
    /// expanded, or <see langword="null"/> when no usable area is configured: the literal is null or
    /// blank, or it expands to a blank string (an unset variable can do that). Never returns an
    /// empty or whitespace path, so a consumer is never handed a blank one. Does not check that the
    /// folder exists. The single point of environment expansion for this field; do not call
    /// <see cref="Environment.ExpandEnvironmentVariables(string)"/> on it elsewhere.</summary>
    /// <param name="expandEnvironmentVariables">Injected expander; defaults to
    /// <see cref="Environment.ExpandEnvironmentVariables(string)"/>. Tests pass a fake so no result
    /// depends on the machine's real environment.</param>
    public string? ResolveDevelopmentArea(Func<string, string>? expandEnvironmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(DevelopmentArea))
        {
            return null;
        }

        var expanded = (expandEnvironmentVariables ?? Environment.ExpandEnvironmentVariables)(DevelopmentArea);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }
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

/// <summary>
/// <c>[hosts.&lt;key&gt;.session]</c>. Every field is optional in TOML, and so is the whole table
/// (bs-aoq, maintainer decision 2026-11-01): <see cref="ConfigParser"/> applies the documented
/// defaults at bind time, so nothing downstream ever sees a null for the booleans. The two
/// colours are the exception by design -- absent means "Terminal's own default", which is
/// represented as <see langword="null"/> and makes the generated profile omit the key.
/// </summary>
public sealed record SessionConfig
{
    public const bool DefaultAutostart = false;
    public const bool DefaultReconnect = true;
    public const bool DefaultTmux = false;

    public bool Autostart { get; init; } = DefaultAutostart;
    public bool Reconnect { get; init; } = DefaultReconnect;
    public bool Tmux { get; init; } = DefaultTmux;

    /// <summary>Only meaningful (and, per the example config, only ever present) when
    /// <see cref="Tmux"/> is true.</summary>
    public string? TmuxSession { get; init; }

    /// <summary>Hex tab colour, or <see langword="null"/> for Terminal's own default. Never an
    /// empty or whitespace string: <see cref="ConfigParser"/> and the host editor both normalise
    /// blank to null.</summary>
    public string? TabColor { get; init; }

    /// <summary>Colour scheme name, or <see langword="null"/> for Terminal's own default. Never
    /// an empty or whitespace string.</summary>
    public string? ColorScheme { get; init; }
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
