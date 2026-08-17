using Bosun.Configuration;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// The field-level edits bs-7ck's classification table is written in terms of, one method per
/// class, so a theory can say "this is a mount-affecting edit" without restating the shape of
/// <see cref="HostConfig"/> eight times.
/// </summary>
/// <remarks>
/// Every edit here is one a <c>ConfigValidator</c>-gated save could actually produce (ADR-019
/// decision 3): no <c>vfs_cache_mode</c> below <c>writes</c> (Invariant I6), no
/// <c>network_mode = false</c> (Invariant I7), no drive-letter collision. An edit the validator
/// would reject never becomes <c>Current</c> and so never reaches the supervisor, and testing one
/// would be testing a config that cannot exist.
/// </remarks>
internal static class ConfigReloadFixtures
{
    /// <summary>The mount-affecting rows of bs-7ck's table: after this edit the live mount no
    /// longer matches intent.</summary>
    public static HostConfig ApplyMountAffecting(HostConfig host, string field) => field switch
    {
        "mount.drive" => host with { Mount = host.Mount with { Drive = "Q:" } },
        "mount.remote_path" => host with { Mount = host.Mount with { RemotePath = "/srv/elsewhere" } },
        "mount.vfs_cache_mode" => host with { Mount = host.Mount with { VfsCacheMode = "full" } },

        // true -> absent. The only legal move: `false` is a validation error (I7), so a config
        // carrying it never becomes Current.
        "mount.network_mode" => host with { Mount = host.Mount with { NetworkMode = null } },

        "hostname" => host with { Hostname = "moved.example.internal" },
        "port" => host with { Port = 2222 },
        "user" => host with { User = "otheruser" },
        "identity_file" => host with { IdentityFile = "~/.ssh/id_ed25519_other" },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a mount-affecting field"),
    };

    /// <summary>The cosmetic / session-only rows: the supervisor's mounts are none of their
    /// business. Each is rewritten into the Terminal fragment by its own path (Invariant I5).</summary>
    public static HostConfig ApplyCosmetic(HostConfig host, string field) => field switch
    {
        "display_name" => host with { DisplayName = "Production (renamed)" },
        "session.tab_color" => host with { Session = host.Session with { TabColor = "#ff8800" } },
        "session.color_scheme" => host with { Session = host.Session with { ColorScheme = "Solarized Dark" } },
        "session.tmux" => host with { Session = host.Session with { Tmux = true } },
        "session.tmux_session" => host with { Session = host.Session with { TmuxSession = "work" } },
        "session.autostart" => host with { Session = host.Session with { Autostart = true } },
        "session.reconnect" => host with { Session = host.Session with { Reconnect = true } },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a cosmetic field"),
    };
}
