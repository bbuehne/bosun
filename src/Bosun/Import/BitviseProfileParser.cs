using System.Text;
using System.Text.RegularExpressions;

namespace Bosun.Import;

/// <summary>
/// Real <see cref="IBitviseProfileParser"/> (bs-ww9.9, ADR-019). Implements the extraction
/// heuristic verified by inspecting 20 real profiles spanning Bitvise/Tunnelier versions 7.16,
/// 9.17, and 9.51 (never checked into this repo -- see the class remarks on
/// <c>tests/Bosun.Tests/Import/BitviseProfileParserTests.cs</c> for why).
/// </summary>
/// <remarks>
/// <para>
/// <b>The format.</b> Length-prefixed records: a big-endian <see cref="int"/> length, then that
/// many bytes. Otherwise undocumented and proprietary -- this class does not attempt a complete
/// parse (ADR-019 is explicit that a brittle full parse is worse than an honest partial one) and
/// only recovers length-prefixed ASCII strings that look like the fields Bosun's host form needs.
/// </para>
/// <para>
/// <b>The heuristic, field by field.</b>
/// </para>
/// <list type="bullet">
/// <item>Scan every byte offset for a plausible big-endian int32 length immediately followed by
/// that many clean, printable ASCII bytes. A match is a candidate string; scanning resumes after
/// it. A non-match advances by a single byte, because the format's other fields are not
/// necessarily string-aligned and the only way to find the next string is to keep looking.</item>
/// <item><b>Hostname</b> = the first candidate string that looks like a dotted hostname or an
/// IPv4 literal and is not the loopback address. Every sample profile had several loopback
/// entries (port-forwarding rules) ahead of the real hostname, so skipping them is essential, not
/// optional.</item>
/// <item><b>Username</b> = the next candidate string after the hostname, in scan order. Verified
/// against every sample profile inspected.</item>
/// <item><b>Port</b> = a big-endian int32 immediately following the username string's bytes, if
/// it falls in the valid TCP port range. This position was not verified against samples the way
/// hostname/username were (bs-ww9.9's brief: "identify it if you can, otherwise leave the default
/// of 22 rather than guessing wrong"), so failing to find one there is treated as "not found," not
/// as an error.</item>
/// </list>
/// </remarks>
public sealed class BitviseProfileParser : IBitviseProfileParser
{
    /// <summary>Guards against a corrupt or hostile length prefix causing a huge allocation --
    /// nothing Bosun cares about in a Bitvise profile is anywhere near this long.</summary>
    private const int MaxPlausibleStringLength = 4096;

    /// <summary>
    /// Addresses that appear in a profile as port-forwarding endpoints rather than as the host
    /// being connected to, and must never be imported as a hostname.
    /// </summary>
    /// <remarks>
    /// <c>127.0.0.1</c> was the only entry until the parser was run against 22 real profiles:
    /// two of them (a listening forward with no loopback rule) yielded <c>0.0.0.0</c> as the
    /// hostname and <c>::</c> as the username — both are bind addresses for a forwarding rule.
    /// Hand-built test fixtures could not have surfaced that, because the fixtures only contained
    /// the shapes already known about. <c>0.0.0.0</c> is the IPv4 any-address; <c>::</c> and
    /// <c>::1</c> are its IPv6 equivalents and appear in the same records.
    /// </remarks>
    private static readonly HashSet<string> NonHostAddresses = new(StringComparer.Ordinal)
    {
        "127.0.0.1",
        "0.0.0.0",
        "::",
        "::1",
    };

    /// <summary>Dotted hostname (each label alphanumeric/hyphen, TLD-like final label) or IPv4
    /// literal. Deliberately does not validate octet ranges for the IPv4 branch -- this is a
    /// heuristic over an undocumented binary format, not a hostname validator, and the samples
    /// this was verified against never needed that precision.</summary>
    private static readonly Regex HostnamePattern = new(
        @"^(?:(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}|(?:\d{1,3}\.){3}\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Matches the header's self-reported version, e.g. "Tunnelier 9.51". Informational
    /// only.</summary>
    /// <summary>An IPv4 literal, used only to disqualify a username that is really an address.</summary>
    private static readonly Regex IpAddressPattern = new(
        @"^(?:\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionPattern = new(
        @"^\S+ \d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public BitviseImportResult Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        List<ExtractedString> strings;
        try
        {
            strings = ExtractLengthPrefixedStrings(data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Belt-and-braces: the scan below is fully bounds-checked and should never throw, but
            // an unrecognized binary format is exactly the kind of input where "should never"
            // is not good enough -- bs-ww9.9's brief requires a reported failure, never a crash.
            return BitviseImportResult.Failed($"Could not scan this file: {ex.Message}");
        }

        if (strings.Count == 0)
        {
            return BitviseImportResult.Failed(
                "No recognizable strings found -- this does not look like a Bitvise/Tunnelier profile.");
        }

        var detectedVersion = strings
            .Select(s => s.Value)
            .FirstOrDefault(s => VersionPattern.IsMatch(s));

        for (var i = 0; i < strings.Count; i++)
        {
            var candidate = strings[i];
            if (!LooksLikeHostname(candidate.Value))
            {
                continue;
            }

            var usernameEntry = i + 1 < strings.Count ? strings[i + 1] : null;

            // The string after the hostname is USUALLY the username, but not always -- on real
            // profiles whose only forwarding entries are any-address bindings it was the peer
            // address instead. An obviously-wrong username pre-filled into the form looks
            // deliberate and gets saved; leaving it blank makes the user supply it.
            if (usernameEntry is not null && !LooksLikeUsername(usernameEntry.Value))
            {
                usernameEntry = null;
            }

            var port = DetectPort(data, usernameEntry) ?? 22;

            return BitviseImportResult.Ok(candidate.Value, usernameEntry?.Value, port, detectedVersion);
        }

        return BitviseImportResult.Failed(
            "Found data in this file, but no usable hostname -- only port-forwarding endpoints, "
            + "or a layout this heuristic does not recognize.");
    }

    private static bool LooksLikeHostname(string candidate) =>
        !NonHostAddresses.Contains(candidate)
        && HostnamePattern.IsMatch(candidate);

    /// <summary>
    /// Rejects a username that is plainly an address rather than an account. When a profile's
    /// only forwarding entries are any-address bindings, the string following the chosen hostname
    /// can be the peer address rather than a user — observed as <c>::</c> on real profiles. Better
    /// to import no username, and leave the field for the user to fill, than to pre-fill a wrong
    /// one that looks deliberate.
    /// </summary>
    /// <remarks>
    /// Rejects ADDRESSES, deliberately not everything matching <see cref="HostnamePattern"/>:
    /// that pattern also matches ordinary <c>first.last</c> usernames, and using it here silently
    /// dropped a real username ("barry.buehne") from one of the maintainer's own profiles.
    /// </remarks>
    private static bool LooksLikeUsername(string candidate) =>
        !NonHostAddresses.Contains(candidate)
        && !IpAddressPattern.IsMatch(candidate)
        && candidate.Length > 0
        && candidate.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '@' or '\\');

    /// <summary>Reads the raw int32 immediately after <paramref name="username"/>'s bytes, if
    /// there is room for one and it falls in the valid TCP port range. Returns
    /// <see langword="null"/> -- never a guessed value -- for anything else, including "no
    /// username was found at all."</summary>
    private static int? DetectPort(byte[] data, ExtractedString? username)
    {
        if (username is null)
        {
            return null;
        }

        var offset = username.Offset + 4 + username.Length;
        if (offset + 4 > data.Length)
        {
            return null;
        }

        var candidate = ReadInt32BigEndian(data, offset);
        return candidate is > 0 and <= 65535 ? candidate : null;
    }

    /// <summary>The core heuristic: scan every byte offset for a big-endian int32 length whose
    /// following bytes are all clean printable ASCII. Non-overlapping on a match (scanning resumes
    /// after the extracted string); advances one byte at a time otherwise, since the format is not
    /// guaranteed to align strings to any particular boundary.</summary>
    private static List<ExtractedString> ExtractLengthPrefixedStrings(byte[] data)
    {
        var results = new List<ExtractedString>();
        var i = 0;

        while (i + 4 <= data.Length)
        {
            var length = ReadInt32BigEndian(data, i);

            // The overrun check is done in `long` arithmetic deliberately: `length` is untrusted
            // input up to int.MaxValue, and `i + 4 + length` computed as plain `int` can wrap
            // around past int.MaxValue and come out negative -- which would then satisfy
            // `<= data.Length` and let IsCleanAscii read past the end of the buffer. Relying on
            // MaxPlausibleStringLength alone to prevent that would make this guard's safety
            // contingent on a second, unrelated bound; relying on the outer try/catch in Parse()
            // to turn the resulting IndexOutOfRangeException into a reported failure would satisfy
            // the "never crash" contract but only by treating an out-of-bounds read as expected
            // control flow. Both are avoided by not letting the addition overflow in the first
            // place.
            if (length > 0
                && length <= MaxPlausibleStringLength
                && (long)i + 4 + length <= data.Length
                && IsCleanAscii(data, i + 4, length))
            {
                var value = Encoding.ASCII.GetString(data, i + 4, length);
                results.Add(new ExtractedString(i, length, value));
                i += 4 + length;
            }
            else
            {
                i += 1;
            }
        }

        return results;
    }

    private static bool IsCleanAscii(byte[] data, int offset, int length)
    {
        for (var j = 0; j < length; j++)
        {
            var b = data[offset + j];
            // Printable ASCII only (space through tilde). Excludes control characters, embedded
            // NULs, and anything above 0x7F -- a real string in this format should not contain any
            // of those, and admitting them would make the heuristic match binary noise.
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadInt32BigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private sealed record ExtractedString(int Offset, int Length, string Value);
}
