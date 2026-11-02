using System.Text.Json;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Support;

namespace Bosun.Tests.Terminal;

/// <summary>bs-4ic: the <c>-p</c> value that opens a host's generated profile is the braced GUID
/// the fragment carries for that host, derived from the config key -- never a name.</summary>
public sealed class TerminalProfileArgumentTests
{
    [Fact]
    public void For_equals_the_guid_string_the_fragment_serialises_for_the_same_host()
    {
        // Through the real serialiser, so a change to the fragment's GUID format or derivation
        // that this helper did not follow fails here.
        var host = TerminalHostFixtures.Host("example-nas", displayName: "Example NAS");

        var json = FragmentSerializer.Serialize(FragmentProfileGenerator.CreateDocument([host]));
        using var document = JsonDocument.Parse(json);
        var emittedGuid = document.RootElement.GetProperty("profiles")[0].GetProperty("guid").GetString();

        Assert.Equal(emittedGuid, TerminalProfileArgument.For("example-nas"));
    }

    [Fact]
    public void For_equals_the_guid_the_generator_derives_even_when_display_name_differs_from_the_key()
    {
        var host = TerminalHostFixtures.Host("example-nas", displayName: "Totally Different Name");

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Equal(profile.Guid.ToString("B"), TerminalProfileArgument.For(host.Key));
        Assert.NotEqual(host.DisplayName, TerminalProfileArgument.For(host.Key));
    }

    [Fact]
    public void For_pins_a_known_value()
    {
        // Independent of this codebase: computed with a separate Python UUIDv5 (UTF-16LE names,
        // fragment namespace f65ddb7e-...), which also reproduces Microsoft's Git/"Git Bash" vector.
        Assert.Equal("{65762cab-0682-50e7-b722-2a7bbfadc2a1}", TerminalProfileArgument.For("example-nas"));
        Assert.Equal("{0454c4de-1541-5eac-93f9-6c8a9a98497d}", TerminalProfileArgument.For("example-remote"));
    }

    [Fact]
    public void For_is_braced_and_lowercase()
    {
        var argument = TerminalProfileArgument.For("example-nas");

        Assert.StartsWith("{", argument);
        Assert.EndsWith("}", argument);
        Assert.Equal(argument.ToLowerInvariant(), argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void For_a_blank_key_throws(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => TerminalProfileArgument.For(key));
    }

    [Fact]
    public void For_a_null_key_throws()
    {
        Assert.Throws<ArgumentNullException>(() => TerminalProfileArgument.For(null!));
    }
}
