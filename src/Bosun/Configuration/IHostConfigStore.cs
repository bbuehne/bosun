namespace Bosun.Configuration;

/// <summary>
/// Owns the live, validated <see cref="BosunConfig"/> for the process (bs-30b;
/// docs/ARCHITECTURE.md §3). Loads and validates at startup, watches <c>hosts.toml</c>, and
/// reloads on write.
/// </summary>
/// <remarks>
/// An invalid config — whether malformed TOML or one that fails a
/// <see cref="ConfigValidator"/> rule — is rejected wholesale and never partially applied
/// (docs/ARCHITECTURE.md §6). The store keeps serving <see cref="Current"/> from the last
/// successful load and raises <see cref="ConfigReloadFailed"/> instead.
/// </remarks>
public interface IHostConfigStore
{
    /// <summary>
    /// The last successfully validated config. Consumers read this snapshot; it never mutates
    /// underneath them — a new config replaces it wholesale, it is never edited in place.
    /// </summary>
    BosunConfig Current { get; }

    /// <summary>Raised after a reload produces a new valid config that has become
    /// <see cref="Current"/>.</summary>
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    /// <summary>Raised when a reload attempt did not produce a usable config — a transient read
    /// failure that exhausted its retries, malformed TOML, or a validation failure.
    /// <see cref="Current"/> is unchanged when this fires.</summary>
    event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
}

public sealed class ConfigChangedEventArgs(BosunConfig config) : EventArgs
{
    public BosunConfig Config { get; } = config;
}

public enum ConfigReloadFailureReason
{
    /// <summary>The file could not be read, or read as empty, and stayed that way through every
    /// retry — most often an editor mid-save. Not a statement about the config's content.</summary>
    ReadFailed,

    /// <summary>The file read cleanly but is not valid TOML, or a value could not be bound to
    /// the schema (see <see cref="ConfigParseException"/>).</summary>
    ParseFailed,

    /// <summary>The file parsed but violates one or more docs/CONFIG-SCHEMA.md rules (see
    /// <see cref="ConfigValidator"/>).</summary>
    ValidationFailed,
}

public sealed class ConfigReloadFailedEventArgs(ConfigReloadFailureReason reason, IReadOnlyList<string> errors) : EventArgs
{
    public ConfigReloadFailureReason Reason { get; } = reason;
    public IReadOnlyList<string> Errors { get; } = errors;
}
