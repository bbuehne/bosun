namespace Bosun.Configuration;

/// <summary>
/// Thrown by <see cref="ConfigParser.Parse"/> when <c>hosts.toml</c> cannot be turned into a
/// <see cref="BosunConfig"/> — either malformed TOML syntax, or a value that cannot be bound
/// into the model (an unrecognized <c>mount.mode</c>, for example). Deliberately distinct from
/// a validation failure (<see cref="ConfigValidator"/>): this is "the file could not be read as
/// this schema at all", not "the file parsed but violates a rule".
/// </summary>
/// <remarks>
/// A config error the user cannot locate is a bad error (bs-0na): where the underlying TOML
/// diagnostics carry a position, <see cref="Line"/>/<see cref="Column"/> are populated and the
/// message is prefixed with it.
/// </remarks>
public sealed class ConfigParseException : Exception
{
    /// <summary>1-based line of the offending token, when known.</summary>
    public int? Line { get; }

    /// <summary>1-based column of the offending token, when known.</summary>
    public int? Column { get; }

    /// <summary>
    /// Every individual diagnostic that contributed to this failure, already formatted with
    /// position where available. Most failures are a single entry; malformed TOML can produce
    /// several.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }

    public ConfigParseException(string message, int? line = null, int? column = null, IReadOnlyList<string>? diagnostics = null)
        : base(message)
    {
        Line = line;
        Column = column;
        Diagnostics = diagnostics ?? [message];
    }

    public ConfigParseException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = [message];
    }
}
