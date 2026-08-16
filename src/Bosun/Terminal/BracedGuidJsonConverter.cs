using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bosun.Terminal;

/// <summary>
/// Serialises a <see cref="Guid"/> as <c>"{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}"</c> (the "B"
/// format), matching the convention Windows Terminal itself uses for profile GUIDs in
/// <c>settings.json</c> and its own documented worked example. <see cref="System.Text.Json"/>'s
/// default <see cref="Guid"/> handling emits the "D" format (no braces), which would still be
/// valid JSON but would not match what a human comparing this fragment against Terminal's own
/// output expects to see.
/// </summary>
internal sealed class BracedGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.Parse(reader.GetString() ?? throw new JsonException("Expected a GUID string."));

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("B"));
}
