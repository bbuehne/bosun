namespace Bosun.Import;

/// <summary>
/// What <see cref="BitviseProfileParser"/> could confidently pull out of a Bitvise/Tunnelier
/// profile (bs-ww9.9, ADR-019). Never thrown for a file the parser cannot understand -- a
/// malformed, truncated, or unrecognized profile is a <see cref="Failed"/> result, not an
/// exception.
/// </summary>
/// <remarks>
/// Deliberately partial. The profile format is undocumented and version-tagged, and ADR-019 is
/// explicit that a full parse is not attempted: only the fields this class is confident about
/// (hostname, username, and -- when it can be identified -- port) are returned. Everything else
/// in the profile (port forwarding, the FTP bridge, window geometry) has no home here because
/// Bosun has no model for it.
/// </remarks>
public sealed class BitviseImportResult
{
    /// <summary>True when a usable hostname was found. False for anything else -- an empty file,
    /// a file with no non-loopback hostname-shaped string, or a file this heuristic simply does
    /// not recognize.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The extracted hostname or IPv4 literal. Only meaningful when <see cref="Succeeded"/>.</summary>
    public string? Hostname { get; init; }

    /// <summary>The extracted username, if the string immediately following the hostname in scan
    /// order was found. <see langword="null"/> when there was nothing after the hostname to read --
    /// never a guess.</summary>
    public string? Username { get; init; }

    /// <summary>The extracted port, or the SSH default (22) when a port could not be confidently
    /// identified -- ADR-019 and bs-ww9.9's brief are explicit that guessing wrong is worse than
    /// defaulting.</summary>
    public int Port { get; init; } = 22;

    /// <summary>The profile's self-reported version string (e.g. "Tunnelier 9.51"), if the header
    /// was recognized. Informational only -- never affects extraction.</summary>
    public string? DetectedVersion { get; init; }

    /// <summary>Human-readable reason extraction did not succeed. Only meaningful when
    /// <see cref="Succeeded"/> is <see langword="false"/>.</summary>
    public string? Error { get; init; }

    public static BitviseImportResult Failed(string error) => new() { Succeeded = false, Error = error };

    public static BitviseImportResult Ok(string hostname, string? username, int port, string? detectedVersion) =>
        new()
        {
            Succeeded = true,
            Hostname = hostname,
            Username = username,
            Port = port,
            DetectedVersion = detectedVersion,
        };
}
