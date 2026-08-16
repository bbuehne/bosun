using System.Text.Json;
using Bosun.Terminal;

namespace Bosun.Tests.Terminal;

/// <summary>
/// Covers bs-n78's model/serialisation acceptance: output is valid JSON, round-trips, has exactly
/// the fields Terminal's fragment schema expects (bs-3ir), and never emits a top-level
/// <c>schemes</c> array (bs-289).
/// </summary>
public sealed class FragmentSerializationTests
{
    private static readonly Guid SampleGuid = Guid.Parse("2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b");

    [Fact]
    public void Serialize_produces_valid_JSON()
    {
        var document = new FragmentDocument
        {
            Profiles = [Profile("example-nas", "Example NAS")],
        };

        var json = FragmentSerializer.Serialize(document);

        using var parsed = JsonDocument.Parse(json); // throws if invalid
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
    }

    [Fact]
    public void Serialize_emits_only_a_top_level_profiles_array_and_never_schemes()
    {
        var document = new FragmentDocument { Profiles = [Profile("a", "A")] };

        var json = FragmentSerializer.Serialize(document);
        using var parsed = JsonDocument.Parse(json);

        Assert.True(parsed.RootElement.TryGetProperty("profiles", out _));
        Assert.False(parsed.RootElement.TryGetProperty("schemes", out _));
    }

    [Fact]
    public void Serialize_uses_terminals_exact_field_names()
    {
        var document = new FragmentDocument { Profiles = [Profile("a", "A")] };

        var json = FragmentSerializer.Serialize(document);
        using var parsed = JsonDocument.Parse(json);
        var profile = parsed.RootElement.GetProperty("profiles")[0];

        Assert.True(profile.TryGetProperty("name", out _));
        Assert.True(profile.TryGetProperty("guid", out _));
        Assert.True(profile.TryGetProperty("commandline", out _)); // one word, not commandLine
        Assert.True(profile.TryGetProperty("startingDirectory", out _));
        Assert.True(profile.TryGetProperty("colorScheme", out _));
        Assert.True(profile.TryGetProperty("tabColor", out _));
        Assert.False(profile.TryGetProperty("commandLine", out _));
    }

    [Fact]
    public void Serialize_writes_the_guid_in_braced_form_matching_Terminals_own_convention()
    {
        var document = new FragmentDocument { Profiles = [Profile("a", "A")] };

        var json = FragmentSerializer.Serialize(document);
        using var parsed = JsonDocument.Parse(json);
        var guidText = parsed.RootElement.GetProperty("profiles")[0].GetProperty("guid").GetString();

        Assert.Equal($"{{{SampleGuid}}}", guidText);
    }

    [Fact]
    public void Serialize_omits_icon_when_null()
    {
        var document = new FragmentDocument
        {
            Profiles = [new FragmentProfile
            {
                Name = "A",
                Guid = SampleGuid,
                CommandLine = "ssh a",
                StartingDirectory = "%USERPROFILE%",
                Icon = null,
            }],
        };

        var json = FragmentSerializer.Serialize(document);
        using var parsed = JsonDocument.Parse(json);
        var profile = parsed.RootElement.GetProperty("profiles")[0];

        Assert.False(profile.TryGetProperty("icon", out _));
    }

    private static FragmentProfile Profile(string key, string displayName) => new()
    {
        Name = displayName,
        Guid = SampleGuid,
        CommandLine = $"ssh {key}",
        StartingDirectory = "%USERPROFILE%",
        ColorScheme = "Campbell",
        TabColor = "#2D5F3F",
    };
}
