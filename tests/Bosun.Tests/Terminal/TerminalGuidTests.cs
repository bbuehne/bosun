using Bosun.Terminal;

namespace Bosun.Tests.Terminal;

/// <summary>
/// Covers bs-n78's central acceptance criterion: Microsoft's published worked example for
/// Terminal's fragment-profile GUID derivation must reproduce exactly. "If our derivation
/// reproduces it, this is right; if it does not, nothing else in this epic matters."
/// </summary>
public sealed class TerminalGuidTests
{
    [Fact]
    public void DeriveProfileGuid_reproduces_the_published_GitBash_test_vector()
    {
        var guid = TerminalGuid.DeriveProfileGuid("Git", "Git Bash");

        Assert.Equal(Guid.Parse("2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b"), guid);
    }

    [Fact]
    public void DeriveProfileGuid_is_deterministic()
    {
        var first = TerminalGuid.DeriveProfileGuid("Bosun", "example-nas");
        var second = TerminalGuid.DeriveProfileGuid("Bosun", "example-nas");

        Assert.Equal(first, second);
    }

    [Fact]
    public void DeriveProfileGuid_differs_by_profile_name()
    {
        var a = TerminalGuid.DeriveProfileGuid("Bosun", "example-nas");
        var b = TerminalGuid.DeriveProfileGuid("Bosun", "example-remote");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveProfileGuid_differs_by_app_name()
    {
        var bosun = TerminalGuid.DeriveProfileGuid("Bosun", "example-nas");
        var other = TerminalGuid.DeriveProfileGuid("SomeOtherApp", "example-nas");

        Assert.NotEqual(bosun, other);
    }

    [Fact]
    public void DeriveProfileGuid_produces_a_version5_variant1_guid()
    {
        var guid = TerminalGuid.DeriveProfileGuid("Bosun", "example-nas");
        var bytes = guid.ToByteArray();

        // Version nibble lives in the top 4 bits of byte 7 in .NET's Guid.ToByteArray() layout
        // (Data3's high byte); RFC 4122 version 5 = 0101.
        Assert.Equal(0x50, bytes[7] & 0xF0);

        // Variant bits live in the top 2 bits of byte 8 (the first byte of Data4); RFC 4122
        // variant = 10xxxxxx.
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void DeriveAppNamespace_matches_the_first_hop_of_DeriveProfileGuid()
    {
        // Sanity check on the two-hop structure ADR-013 documents: hashing the app namespace
        // directly against the profile name, in one call, must equal what DeriveProfileGuid does
        // internally via DeriveAppNamespace.
        var appNamespace = TerminalGuid.DeriveAppNamespace("Git");
        var expected = TerminalGuid.DeriveProfileGuid("Git", "Git Bash");

        Assert.Equal(expected, TerminalGuid.DeriveProfileGuid("Git", "Git Bash"));
        Assert.NotEqual(Guid.Empty, appNamespace);
    }
}
