using System.Globalization;
using System.Text;

namespace Bosun.Configuration;

/// <summary>
/// Serializes a <see cref="BosunConfig"/> back into <c>hosts.toml</c> text (bs-ww9.8, ADR-019).
/// There is no Tomlyn writer wired up for this project (only its deserializer is used, by
/// <see cref="ConfigParser"/>), so this hand-writes TOML shaped like <c>config/hosts.example.toml</c>
/// -- a human still edits this file by hand between Bosun runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Round-trip fidelity is the whole point of this class</b> (see
/// <c>HostConfigWriteRoundTripTests</c>): <c>ConfigParser.Parse(Write(config))</c> must bind back
/// to a <see cref="BosunConfig"/> equivalent to <paramref name="config"/> in every test that calls
/// this class's <see cref="Write"/> method. <see cref="HostConfigStore.AdoptSelfWrite"/> trusts
/// this — it adopts the in-memory <see cref="BosunConfig"/> as <see cref="HostConfigStore.Current"/>
/// immediately, on the assumption that re-parsing the text this class just produced would yield
/// the same thing.
/// </para>
/// <para>
/// <b>Nullable fields are omitted, not written as an empty/placeholder value.</b> A field absent
/// from TOML and a field written as <c>null</c> are different concepts in TOML (which has no
/// literal null) — the only faithful way to represent "this was absent" is to not emit the key at
/// all, exactly as <see cref="ConfigParser"/> expects (<c>MountConfigRaw</c>/<c>SessionConfigRaw</c>
/// fields default to <see langword="null"/> when their TOML key is missing).
/// </para>
/// <para>
/// <b>Strings are escaped defensively, not just for the values this codebase happens to produce
/// today.</b> Windows paths (<c>identity_file</c>) contain backslashes, and
/// <c>ConfigValidatorAdversarialTests</c> proves a TOML basic string can carry a literal control
/// character (e.g. an escaped newline) through parsing into a field value — see
/// <see cref="EscapeTomlString"/>.
/// </para>
/// </remarks>
internal static class HostConfigTomlWriter
{
    public static string Write(BosunConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sb = new StringBuilder();
        WriteGlobal(sb, config.Global);

        foreach (var (key, host) in config.Hosts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append('\n');
            WriteHost(sb, key, host);
        }

        return sb.ToString();
    }

    private static void WriteGlobal(StringBuilder sb, GlobalConfig global)
    {
        sb.Append("[global]\n");
        AppendInt(sb, "rclone_rc_port", global.RcloneRcPort);
        AppendString(sb, "rclone_config_path", global.RcloneConfigPath);
        AppendInt(sb, "probe_timeout_seconds", global.ProbeTimeoutSeconds);
        AppendInt(sb, "failures_before_unmount", global.FailuresBeforeUnmount);
        AppendIntArray(sb, "backoff_seconds", global.BackoffSeconds);
        AppendInt(sb, "mounted_probe_interval_seconds", global.MountedProbeIntervalSeconds);
        AppendInt(sb, "mounted_deep_probe_interval_seconds", global.MountedDeepProbeIntervalSeconds);
        AppendInt(sb, "suspend_unmount_timeout_seconds", global.SuspendUnmountTimeoutSeconds);
        AppendBool(sb, "start_with_windows", global.StartWithWindows);
    }

    private static void WriteHost(StringBuilder sb, string key, HostConfig host)
    {
        var tomlKey = TomlKeyPathSegment(key);

        sb.Append('[').Append("hosts.").Append(tomlKey).Append(']').Append('\n');
        AppendString(sb, "display_name", host.DisplayName);
        AppendString(sb, "hostname", host.Hostname);
        AppendInt(sb, "port", host.Port);
        AppendString(sb, "user", host.User);
        AppendString(sb, "identity_file", host.IdentityFile);
        sb.Append('\n');

        sb.Append("  [").Append("hosts.").Append(tomlKey).Append(".mount]\n");
        AppendString(sb, "mode", ToTomlValue(host.Mount.Mode), indent: "  ");
        AppendStringIfNotNull(sb, "drive", host.Mount.Drive, indent: "  ");
        AppendStringIfNotNull(sb, "remote_path", host.Mount.RemotePath, indent: "  ");
        AppendStringIfNotNull(sb, "vfs_cache_mode", host.Mount.VfsCacheMode, indent: "  ");
        AppendBoolIfNotNull(sb, "network_mode", host.Mount.NetworkMode, indent: "  ");
        AppendIntIfNotNull(sb, "idle_unmount_seconds", host.Mount.IdleUnmountSeconds, indent: "  ");
        sb.Append('\n');

        sb.Append("  [").Append("hosts.").Append(tomlKey).Append(".session]\n");
        AppendBool(sb, "autostart", host.Session.Autostart, indent: "  ");
        AppendBool(sb, "reconnect", host.Session.Reconnect, indent: "  ");
        AppendBool(sb, "tmux", host.Session.Tmux, indent: "  ");
        AppendStringIfNotNull(sb, "tmux_session", host.Session.TmuxSession, indent: "  ");
        AppendString(sb, "tab_color", host.Session.TabColor, indent: "  ");
        AppendString(sb, "color_scheme", host.Session.ColorScheme, indent: "  ");
        sb.Append('\n');

        sb.Append("  [").Append("hosts.").Append(tomlKey).Append(".probe]\n");
        AppendInt(sb, "interval_seconds", host.Probe.IntervalSeconds, indent: "  ");
        AppendBool(sb, "deep_probe", host.Probe.DeepProbe, indent: "  ");
    }

    private static string ToTomlValue(MountMode mode) => mode switch
    {
        MountMode.Persistent => "persistent",
        MountMode.OnDemand => "on-demand",
        MountMode.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unrecognized MountMode"),
    };

    private static void AppendString(StringBuilder sb, string key, string value, string indent = "") =>
        sb.Append(indent).Append(key).Append(" = ").Append(EscapeTomlString(value)).Append('\n');

    private static void AppendStringIfNotNull(StringBuilder sb, string key, string? value, string indent = "")
    {
        if (value is not null)
        {
            AppendString(sb, key, value, indent);
        }
    }

    private static void AppendInt(StringBuilder sb, string key, int value, string indent = "") =>
        sb.Append(indent).Append(key).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void AppendIntIfNotNull(StringBuilder sb, string key, int? value, string indent = "")
    {
        if (value is not null)
        {
            AppendInt(sb, key, value.Value, indent);
        }
    }

    private static void AppendBool(StringBuilder sb, string key, bool value, string indent = "") =>
        sb.Append(indent).Append(key).Append(" = ").Append(value ? "true" : "false").Append('\n');

    private static void AppendBoolIfNotNull(StringBuilder sb, string key, bool? value, string indent = "")
    {
        if (value is not null)
        {
            AppendBool(sb, key, value.Value, indent);
        }
    }

    private static void AppendIntArray(StringBuilder sb, string key, IReadOnlyList<int> values, string indent = "")
    {
        var joined = string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        sb.Append(indent).Append(key).Append(" = [").Append(joined).Append(']').Append('\n');
    }

    // TOML bare keys are [A-Za-z0-9_-]+. A host key of that shape is written unquoted, matching
    // config/hosts.example.toml's style for hand-editability; anything else (spaces, dots, etc.)
    // is written as a quoted dotted-key segment, which TOML permits mixed with bare segments in
    // the same dotted path ([hosts."my key".mount] is legal) and which ConfigParser/Tomlyn binds
    // identically to a bare key.
    private static bool IsBareKey(string key) => key.Length > 0 && key.All(IsBareKeyChar);

    private static bool IsBareKeyChar(char c) =>
        (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '-' or '_';

    private static string TomlKeyPathSegment(string key) => IsBareKey(key) ? key : EscapeTomlString(key);

    // Mirrors TOML's basic-string escaping rules. Defensive beyond what this codebase's own
    // fields happen to contain today: identity_file is a Windows path (backslashes), and
    // ConfigValidatorAdversarialTests proves a field value can already carry a literal control
    // character (an embedded \n) through parsing -- a naive writer that assumed "no escaping
    // needed, these are just names and paths" would emit invalid TOML (an unescaped backslash or
    // a raw newline inside a single-line basic string) the moment such a value round-tripped.
    private static string EscapeTomlString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
