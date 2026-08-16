using System.Security.Cryptography;
using System.Text;

namespace Bosun.Terminal;

/// <summary>
/// Reproduces Windows Terminal's fragment-profile GUID derivation (ADR-013; verified research on
/// bs-3ir). Terminal computes a UUIDv5 in two hops:
/// <code>
/// appNamespace = UUIDv5(TERMINAL_FRAGMENT_NS, app-name)
/// profileGuid  = UUIDv5(appNamespace, profile-name)
/// </code>
/// with <c>TERMINAL_FRAGMENT_NS</c> = <c>{f65ddb7e-706b-4499-8a50-40313caf510a}</c> and every name
/// string encoded UTF-16LE before hashing (a documented deviation from RFC 4122's usual UTF-8 --
/// this is Terminal's own scheme, not the generic algorithm).
/// </summary>
/// <remarks>
/// .NET has no built-in UUIDv5, so this is a from-scratch RFC 4122 §4.3 implementation: SHA-1 over
/// (namespace bytes in RFC "network byte order" ++ UTF-16LE name bytes), then the version (0101)
/// and variant (10) bits forced into the truncated 16-byte hash.
///
/// <para>
/// <b>Byte order.</b> <see cref="Guid"/>'s in-memory layout stores its first three fields
/// (<c>Data1</c>/<c>Data2</c>/<c>Data3</c>) little-endian, but RFC 4122 defines a UUID's wire/hash
/// representation as those same fields big-endian (network byte order); the trailing 8 bytes
/// (<c>Data4</c>) are an opaque byte sequence with no endianness to flip. <see cref="SwapFieldOrder"/>
/// reverses exactly those first three fields and is applied twice: once converting the namespace
/// GUID from .NET's layout into RFC order before hashing, and once converting the hash's output
/// (which the algorithm treats as already being in RFC order) back into .NET's layout so
/// <c>new Guid(bytes)</c> constructs the right value.
/// </para>
/// <para>
/// Verified against Microsoft's published worked example: app <c>"Git"</c>, profile
/// <c>"Git Bash"</c> -&gt; <c>{2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b}</c> (see
/// <c>TerminalGuidTests.DeriveProfileGuid_reproduces_the_published_Git_Bash_test_vector</c>).
/// </para>
/// </remarks>
public static class TerminalGuid
{
    /// <summary>The fixed namespace UUID every fragment app-namespace is derived from.</summary>
    public static readonly Guid FragmentNamespace = Guid.Parse("f65ddb7e-706b-4499-8a50-40313caf510a");

    /// <summary>First hop: the app-specific namespace GUID Terminal derives all of one app's
    /// profile GUIDs from.</summary>
    public static Guid DeriveAppNamespace(string appName) => CreateVersion5(FragmentNamespace, appName);

    /// <summary>Both hops at once: the final profile GUID for <paramref name="profileName"/> under
    /// app <paramref name="appName"/>.</summary>
    public static Guid DeriveProfileGuid(string appName, string profileName)
    {
        var appNamespace = DeriveAppNamespace(appName);
        return CreateVersion5(appNamespace, profileName);
    }

    private static Guid CreateVersion5(Guid namespaceId, string name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        if (!namespaceId.TryWriteBytes(namespaceBytes))
        {
            throw new InvalidOperationException("Failed to extract namespace GUID bytes.");
        }

        SwapFieldOrder(namespaceBytes); // .NET layout -> RFC 4122 (network) byte order

        var nameBytes = Encoding.Unicode.GetBytes(name); // UTF-16LE, per Terminal's documented scheme

        var buffer = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(buffer);
        nameBytes.CopyTo(buffer.AsSpan(namespaceBytes.Length));

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(buffer, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // variant RFC 4122 (10xxxxxx)

        SwapFieldOrder(guidBytes); // RFC 4122 (network) byte order -> .NET layout
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Reverses the byte order of <see cref="Guid"/>'s first three fields (the 4-byte, 2-byte, and
    /// 2-byte groups) in place, converting between .NET's little-endian in-memory layout and RFC
    /// 4122's big-endian wire layout. The trailing 8 bytes are untouched -- they are already the
    /// same in both representations. Its own inverse: applying it twice is a no-op.
    /// </summary>
    private static void SwapFieldOrder(Span<byte> guidBytes)
    {
        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}
