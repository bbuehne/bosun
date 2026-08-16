using Tomlyn;
using Tomlyn.Syntax;

namespace Bosun.Configuration;

/// <summary>
/// Binds <c>config/hosts.toml</c> text into a <see cref="BosunConfig"/> (bs-0na). Parse and bind
/// only — no cross-field/cross-host validation here, that is <see cref="ConfigValidator"/>
/// (bs-c0g). A pure function: no file I/O, no environment access.
/// </summary>
public static class ConfigParser
{
    /// <summary>
    /// Parses and binds TOML text into a <see cref="BosunConfig"/>, applying documented
    /// <c>[global]</c> defaults (docs/CONFIG-SCHEMA.md) for any absent global key.
    /// </summary>
    /// <param name="tomlText">The raw TOML document.</param>
    /// <param name="sourceName">Name reported in diagnostics — typically the file path.</param>
    /// <exception cref="ConfigParseException">
    /// The text is not valid TOML, or a value cannot be bound into the schema (an unrecognized
    /// <c>mount.mode</c>, a field required by the schema but absent, etc.). Where the underlying
    /// TOML diagnostics carry a position, <see cref="ConfigParseException.Line"/>/
    /// <see cref="ConfigParseException.Column"/> are populated.
    /// </exception>
    public static BosunConfig Parse(string tomlText, string sourceName = "hosts.toml")
    {
        ArgumentNullException.ThrowIfNull(tomlText);

        BosunConfigRaw? raw;
        try
        {
            var options = new TomlSerializerOptions { SourceName = sourceName };
            raw = TomlSerializer.Deserialize<BosunConfigRaw>(tomlText, options);
        }
        catch (TomlException ex)
        {
            throw ToConfigParseException(ex, sourceName);
        }

        raw ??= new BosunConfigRaw();

        var global = MapGlobal(raw.Global);

        var hosts = new Dictionary<string, HostConfig>();
        if (raw.Hosts is not null)
        {
            foreach (var (key, hostRaw) in raw.Hosts)
            {
                hosts[key] = MapHost(key, hostRaw);
            }
        }

        return new BosunConfig
        {
            Global = global,
            Hosts = hosts,
        };
    }

    private static GlobalConfig MapGlobal(GlobalConfigRaw? raw) => new()
    {
        RcloneRcPort = raw?.RcloneRcPort ?? GlobalConfig.DefaultRcloneRcPort,
        RcloneConfigPath = raw?.RcloneConfigPath ?? GlobalConfig.DefaultRcloneConfigPath,
        ProbeTimeoutSeconds = raw?.ProbeTimeoutSeconds ?? GlobalConfig.DefaultProbeTimeoutSeconds,
        FailuresBeforeUnmount = raw?.FailuresBeforeUnmount ?? GlobalConfig.DefaultFailuresBeforeUnmount,
        // An explicit (even empty) list must survive as-is so ConfigValidator can reject an
        // empty ladder; only a wholly absent key falls back to the documented default.
        BackoffSeconds = raw?.BackoffSeconds is { } list ? list.AsReadOnly() : GlobalConfig.DefaultBackoffSeconds,
        MountedProbeIntervalSeconds = raw?.MountedProbeIntervalSeconds ?? GlobalConfig.DefaultMountedProbeIntervalSeconds,
        SuspendUnmountTimeoutSeconds = raw?.SuspendUnmountTimeoutSeconds ?? GlobalConfig.DefaultSuspendUnmountTimeoutSeconds,
        StartWithWindows = raw?.StartWithWindows ?? GlobalConfig.DefaultStartWithWindows,
    };

    private static HostConfig MapHost(string key, HostConfigRaw raw)
    {
        var mountRaw = RequireTable(raw.Mount, $"hosts.{key}.mount");
        var sessionRaw = RequireTable(raw.Session, $"hosts.{key}.session");
        var probeRaw = RequireTable(raw.Probe, $"hosts.{key}.probe");

        return new HostConfig
        {
            Key = key,
            DisplayName = RequireField(raw.DisplayName, $"hosts.{key}.display_name"),
            Hostname = RequireField(raw.Hostname, $"hosts.{key}.hostname"),
            Port = RequireField(raw.Port, $"hosts.{key}.port"),
            User = RequireField(raw.User, $"hosts.{key}.user"),
            IdentityFile = RequireField(raw.IdentityFile, $"hosts.{key}.identity_file"),
            Mount = new MountConfig
            {
                Mode = ParseMountMode(mountRaw.Mode, key),
                Drive = mountRaw.Drive,
                RemotePath = mountRaw.RemotePath,
                VfsCacheMode = mountRaw.VfsCacheMode,
                NetworkMode = mountRaw.NetworkMode,
                IdleUnmountSeconds = mountRaw.IdleUnmountSeconds,
            },
            Session = new SessionConfig
            {
                Autostart = RequireField(sessionRaw.Autostart, $"hosts.{key}.session.autostart"),
                Reconnect = RequireField(sessionRaw.Reconnect, $"hosts.{key}.session.reconnect"),
                Tmux = RequireField(sessionRaw.Tmux, $"hosts.{key}.session.tmux"),
                TmuxSession = sessionRaw.TmuxSession,
                TabColor = RequireField(sessionRaw.TabColor, $"hosts.{key}.session.tab_color"),
                ColorScheme = RequireField(sessionRaw.ColorScheme, $"hosts.{key}.session.color_scheme"),
            },
            Probe = new ProbeConfig
            {
                IntervalSeconds = RequireField(probeRaw.IntervalSeconds, $"hosts.{key}.probe.interval_seconds"),
                DeepProbe = RequireField(probeRaw.DeepProbe, $"hosts.{key}.probe.deep_probe"),
            },
        };
    }

    private static MountMode ParseMountMode(string? mode, string hostKey)
    {
        var raw = RequireField(mode, $"hosts.{hostKey}.mount.mode");
        return raw switch
        {
            "persistent" => MountMode.Persistent,
            "on-demand" => MountMode.OnDemand,
            "none" => MountMode.None,
            _ => throw new ConfigParseException(
                $"hosts.{hostKey}.mount.mode: unrecognized value '{raw}' (expected 'persistent', 'on-demand', or 'none')"),
        };
    }

    private static string RequireField(string? value, string fieldPath)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ConfigParseException($"{fieldPath} is required");
        }

        return value;
    }

    private static T RequireField<T>(T? value, string fieldPath) where T : struct
    {
        if (value is null)
        {
            throw new ConfigParseException($"{fieldPath} is required");
        }

        return value.Value;
    }

    private static T RequireTable<T>(T? value, string fieldPath) where T : class
    {
        if (value is null)
        {
            throw new ConfigParseException($"[{fieldPath}] is required");
        }

        return value;
    }

    private static ConfigParseException ToConfigParseException(TomlException ex, string sourceName)
    {
        var diagnostics = ex.Diagnostics;
        if (diagnostics is { Count: > 0 })
        {
            var formatted = new List<string>(diagnostics.Count);
            foreach (var diagnostic in diagnostics)
            {
                formatted.Add(FormatDiagnostic(diagnostic, sourceName));
            }

            var first = diagnostics[0];
            // Tomlyn.Syntax.TextPosition is zero-based; diagnostics to the user are 1-based.
            return new ConfigParseException(
                string.Join(Environment.NewLine, formatted),
                line: first.Span.Start.Line + 1,
                column: first.Span.Start.Column + 1,
                diagnostics: formatted);
        }

        // TomlException.Line/Column (when the exception carries no diagnostics bag) are already
        // 1-based per Tomlyn's own contract.
        var message = ex.Line is int line && ex.Column is int column
            ? $"{sourceName}:{line}:{column}: {ex.Message}"
            : ex.Message;
        return new ConfigParseException(message, ex.Line, ex.Column, [message]);
    }

    private static string FormatDiagnostic(DiagnosticMessage diagnostic, string sourceName)
    {
        var line = diagnostic.Span.Start.Line + 1;
        var column = diagnostic.Span.Start.Column + 1;
        var fileName = string.IsNullOrEmpty(diagnostic.Span.FileName) ? sourceName : diagnostic.Span.FileName;
        return $"{fileName}:{line}:{column}: {diagnostic.Message}";
    }
}
