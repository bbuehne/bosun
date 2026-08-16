using System.Text.Json;

namespace Bosun.Terminal;

/// <summary>Serialises a <see cref="FragmentDocument"/> to the JSON text Terminal expects.</summary>
public static class FragmentSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static string Serialize(FragmentDocument document) => JsonSerializer.Serialize(document, Options);
}
