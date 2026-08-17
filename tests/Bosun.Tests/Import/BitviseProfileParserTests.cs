using System.Text;
using Bosun.Import;

namespace Bosun.Tests.Import;

/// <summary>
/// <see cref="BitviseProfileParser"/> (bs-ww9.9, ADR-019). Every fixture here is hand-built --
/// never a copy of the maintainer's real Bitvise/Tunnelier profiles, which are personal data and
/// must never enter this repo. The byte layout below (length-prefixed big-endian ASCII strings)
/// mirrors what ADR-019 documents as verified against 20 real profiles spanning versions 7.16,
/// 9.17, and 9.51: this class tests the heuristic's *behaviour*, not any particular file.
/// </summary>
public sealed class BitviseProfileParserTests
{
    private readonly BitviseProfileParser _parser = new();

    /// <summary>Appends a big-endian length-prefixed ASCII string, exactly as the real format is
    /// documented (ADR-019).</summary>
    private static void WriteString(MemoryStream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteInt32BigEndian(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32BigEndian(MemoryStream stream, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        stream.Write(bytes);
    }

    private static byte[] BuildProfile(params string[] strings)
    {
        using var stream = new MemoryStream();
        foreach (var s in strings)
        {
            WriteString(stream, s);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Found by running this parser against 22 real profiles, which hand-built fixtures could not
    /// have surfaced: two of them yielded <c>0.0.0.0</c> as the hostname and <c>::</c> as the
    /// username. Both are bind addresses for a port-forwarding rule, not the host being connected
    /// to. Only <c>127.0.0.1</c> was skipped before.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("127.0.0.1")]
    public void Parse_SkipsPortForwardingBindAddresses_NotJustLoopback(string bindAddress)
    {
        var data = BuildProfile(
            "Tunnelier 9.51",
            bindAddress,
            "realhost.example.com",
            "someuser");

        var result = _parser.Parse(data);

        Assert.True(result.Succeeded);
        Assert.Equal("realhost.example.com", result.Hostname);
    }

    /// <summary>
    /// The string after the hostname is usually the username, but on a profile whose forwarding
    /// entries are any-address bindings it was the peer address instead. Importing an obviously
    /// wrong username is worse than importing none: pre-filled, it looks deliberate and gets
    /// saved, whereas a blank field makes the user supply it.
    /// </summary>
    [Fact]
    public void Parse_LeavesTheUsernameBlank_RatherThanImportingAnAddressAsOne()
    {
        var data = BuildProfile(
            "Tunnelier 9.51",
            "realhost.example.com",
            "::");

        var result = _parser.Parse(data);

        Assert.True(result.Succeeded);
        Assert.Equal("realhost.example.com", result.Hostname);
        Assert.Null(result.Username);
    }

    /// <summary>
    /// A first.last username must survive. Rejecting anything that matched the HOSTNAME pattern
    /// looked like a reasonable way to keep addresses out of the username field, and it silently
    /// dropped "barry.buehne" from one of the maintainer's own profiles -- caught only by running
    /// the parser over real files. Only addresses disqualify a username.
    /// </summary>
    [Theory]
    [InlineData("barry.buehne")]
    [InlineData("barry_buehne")]
    [InlineData("first.last.name")]
    public void Parse_KeepsADottedUsername_WhichIsNotAnAddress(string username)
    {
        var data = BuildProfile(
            "Tunnelier 9.51",
            "realhost.example.com",
            username);

        var result = _parser.Parse(data);

        Assert.True(result.Succeeded);
        Assert.Equal(username, result.Username);
    }

    [Fact]
    public void Parse_ExtractsHostnameAndUsername_SkippingTheLoopbackPortForwardingEntry()
    {
        var data = BuildProfile(
            "Tunnelier 9.51",
            "127.0.0.1",
            "traininggrounds.local",
            "bbuehne");

        var result = _parser.Parse(data);

        Assert.True(result.Succeeded);
        Assert.Equal("traininggrounds.local", result.Hostname);
        Assert.Equal("bbuehne", result.Username);
        Assert.Equal(22, result.Port); // nothing found to identify a port -> default
    }

    [Fact]
    public void Parse_SkipsMultipleLoopbackEntries_BeforeFindingTheRealHostname()
    {
        // Every real sample profile had SEVERAL loopback port-forwarding entries ahead of the
        // real hostname (bs-ww9.9's brief). This is the case that motivated the explicit skip.
        var data = BuildProfile(
            "Tunnelier 9.17",
            "127.0.0.1",
            "localforward-user",
            "127.0.0.1",
            "another-local-user",
            "www.lucid-forge.com",
            "ubuntu");

        var result = _parser.Parse(data);

        Assert.True(result.Succeeded);
        Assert.Equal("www.lucid-forge.com", result.Hostname);
        Assert.Equal("ubuntu", result.Username);
    }

    [Fact]
    public void Parse_RecognizesIPv4Hostnames()
    {
        var data = BuildProfile("Tunnelier 7.16", "127.0.0.1", "root", "192.168.0.70", "bbuehne");

        var result = _parser.Parse(data);

        Assert.True(result.Succeeded);
        Assert.Equal("192.168.0.70", result.Hostname);
        Assert.Equal("bbuehne", result.Username);
    }

    [Fact]
    public void Parse_DetectsPort_WhenARawInt32ImmediatelyFollowsTheUsernameString()
    {
        using var stream = new MemoryStream();
        WriteString(stream, "Tunnelier 9.51");
        WriteString(stream, "127.0.0.1");
        WriteString(stream, "mccharm.com");
        WriteString(stream, "ubuntu");
        WriteInt32BigEndian(stream, 2222); // immediately follows the username's bytes

        var result = _parser.Parse(stream.ToArray());

        Assert.True(result.Succeeded);
        Assert.Equal("mccharm.com", result.Hostname);
        Assert.Equal("ubuntu", result.Username);
        Assert.Equal(2222, result.Port);
    }

    [Fact]
    public void Parse_DoesNotTreatAnOutOfRangeIntAfterUsername_AsAPort()
    {
        using var stream = new MemoryStream();
        WriteString(stream, "traininggrounds.local");
        WriteString(stream, "bbuehne");
        WriteInt32BigEndian(stream, 99999); // not a valid TCP port

        var result = _parser.Parse(stream.ToArray());

        Assert.True(result.Succeeded);
        Assert.Equal(22, result.Port);
    }

    [Fact]
    public void Parse_DetectsVersionHeader_WhenPresent()
    {
        var data = BuildProfile("Tunnelier 9.51", "127.0.0.1", "root", "mccharm.com", "ubuntu");

        var result = _parser.Parse(data);

        Assert.Equal("Tunnelier 9.51", result.DetectedVersion);
    }

    [Fact]
    public void Parse_Fails_WhenOnlyLoopbackHostsArePresent()
    {
        var data = BuildProfile("Tunnelier 9.51", "127.0.0.1", "root", "127.0.0.1", "someone");

        var result = _parser.Parse(data);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Null(result.Hostname);
    }

    [Fact]
    public void Parse_Fails_WhenNoStringsLookLikeAHostname()
    {
        // No dots anywhere -- nothing here should ever be mistaken for a hostname.
        var data = BuildProfile("Tunnelier 9.51", "root", "bbuehne", "somekeyname");

        var result = _parser.Parse(data);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Parse_Fails_ForEmptyInput()
    {
        var result = _parser.Parse([]);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_Fails_ForTruncatedInput()
    {
        // A length prefix claiming 50 bytes follow, but the buffer ends immediately after it.
        byte[] data = [0, 0, 0, 50];

        var result = _parser.Parse(data);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Parse_DoesNotThrowOrHang_ForAnAbsurdLengthPrefix()
    {
        // int.MaxValue as a length prefix must never be trusted enough to allocate against.
        using var stream = new MemoryStream();
        WriteInt32BigEndian(stream, int.MaxValue);
        stream.Write(Encoding.ASCII.GetBytes("not enough bytes to matter"));

        var exception = Record.Exception(() => _parser.Parse(stream.ToArray()));

        Assert.Null(exception);
    }

    [Fact]
    public void Parse_RejectsAnImplausiblyLongDeclaredField_EvenWhenTheBufferGenuinelyHasThatManyBytes()
    {
        // Regression coverage for the sanity bound (MaxPlausibleStringLength): a 200,000-byte
        // "field" is not the kind of thing this format's fields look like, even when the length
        // prefix is honest and the bytes really are clean ASCII. NOTE, found during mutation
        // testing: this assertion is also true with the bound removed, because the resulting
        // 200,000-byte blob is dominated by filler and never matches the anchored hostname regex
        // either way -- so this test does not, by itself, prove the bound is load-bearing. See
        // BitviseProfileParser's remarks on the overrun check for why a black-box test cannot
        // cleanly isolate this bound from IsCleanAscii's own strictness.
        const int implausiblyLongButReal = 200_000;
        using var stream = new MemoryStream();
        WriteInt32BigEndian(stream, implausiblyLongButReal);
        stream.Write(new byte[implausiblyLongButReal].Select(_ => (byte)'a').ToArray());

        var result = _parser.Parse(stream.ToArray());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Parse_RecoversAfterGarbageBytes_AndFindsAHostnameLaterInTheFile()
    {
        // Simulates the parts of the format this parser does not understand: noise ahead of a
        // real, well-formed record. The byte-at-a-time fallback scan must still find it.
        using var stream = new MemoryStream();
        stream.Write([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03]);
        WriteString(stream, "traininggrounds.local");
        WriteString(stream, "bbuehne");

        var result = _parser.Parse(stream.ToArray());

        Assert.True(result.Succeeded);
        Assert.Equal("traininggrounds.local", result.Hostname);
    }

    [Fact]
    public void Parse_IgnoresLengthPrefixedNonAsciiContent()
    {
        using var stream = new MemoryStream();
        // A "string" whose length prefix is valid but whose content is not clean ASCII.
        var nonAsciiBytes = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };
        WriteInt32BigEndian(stream, nonAsciiBytes.Length);
        stream.Write(nonAsciiBytes);
        WriteString(stream, "traininggrounds.local");
        WriteString(stream, "bbuehne");

        var result = _parser.Parse(stream.ToArray());

        Assert.True(result.Succeeded);
        Assert.Equal("traininggrounds.local", result.Hostname);
    }

    [Fact]
    public void Parse_ThrowsArgumentNullException_ForNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Parse(null!));
    }
}
