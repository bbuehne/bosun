using System.Text.Json.Serialization;

namespace Bosun.Terminal;

/// <summary>
/// The top-level shape of a Windows Terminal fragment file (bs-3ir, verified against Microsoft
/// Learn): an object with a <c>profiles</c> array. Deliberately no <c>schemes</c> array -- bs-289
/// decided Bosun only ever references an existing scheme by name; shipping our own risks colliding
/// in Terminal's single global scheme namespace, and collision behaviour there is undocumented.
/// </summary>
public sealed record FragmentDocument
{
    [JsonPropertyName("profiles")]
    public required IReadOnlyList<FragmentProfile> Profiles { get; init; }
}

/// <summary>
/// One Terminal profile entry. Field set and JSON names match Terminal's fragment schema exactly
/// (bs-3ir): <c>name</c>, <c>guid</c>, <c>commandline</c> (one word, not <c>commandLine</c>),
/// <c>startingDirectory</c>, <c>icon</c>, <c>colorScheme</c>, <c>tabColor</c>.
/// </summary>
/// <remarks>
/// <see cref="Guid"/> is emitted explicitly (ADR-013) rather than left for Terminal to derive from
/// <see cref="Name"/> -- see <see cref="TerminalGuid"/> and <see cref="FragmentProfileGenerator"/>
/// for why (the derivation input is the host's stable config key, not the display name that ends up
/// in <see cref="Name"/>).
///
/// <see cref="StartingDirectory"/> is required and always set: Terminal's own docs self-contradict
/// on its default (stating <c>%USERPROFILE%</c> in one place, then listing four
/// launch-context-dependent defaults elsewhere), so relying on it is relying on something
/// undocumented.
/// </remarks>
public sealed record FragmentProfile
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("guid")]
    [JsonConverter(typeof(BracedGuidJsonConverter))]
    public required Guid Guid { get; init; }

    [JsonPropertyName("commandline")]
    public required string CommandLine { get; init; }

    [JsonPropertyName("startingDirectory")]
    public required string StartingDirectory { get; init; }

    /// <summary>Deliberately never populated (bs-9fs). Per-host icons were considered and
    /// declined: tab colour already differentiates hosts, and CLAUDE.md §5 says not to add
    /// options this tool's one user has not asked for. Kept on the model because it is part of
    /// Terminal's profile schema and omitting it from the type would be a lie about the format
    /// rather than about Bosun; the serialiser drops it when null, so nothing is emitted.
    /// Terminal accepts either a file path or an emoji here if this is ever revisited.</summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    [JsonPropertyName("colorScheme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorScheme { get; init; }

    [JsonPropertyName("tabColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TabColor { get; init; }
}
