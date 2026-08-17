using System.IO;
using System.Text;

namespace Bosun.Import;

/// <summary>
/// Derives a new host's short name (the TOML key / ssh-config alias, ADR-013) from an imported
/// Bitvise/Tunnelier profile's file name (bs-ww9.9, ADR-019): "TrainingGrounds.tlp" becomes
/// "traininggrounds", because that is usually what the profile's ssh-config alias is called too.
/// </summary>
/// <remarks>
/// Pure and deliberately separate from the file dialog -- this only ever sees a file name, never a
/// path, and is unit-tested directly.
/// </remarks>
public static class BitviseShortNameDeriver
{
    private const string Fallback = "imported-host";

    /// <summary>Lowercases the file name's stem (extension stripped) and replaces every run of
    /// non-alphanumeric characters with a single hyphen, trimming leading/trailing hyphens. Falls
    /// back to <see cref="Fallback"/> if nothing alphanumeric survives. Only has to be a reasonable
    /// starting point, not a guaranteed-unique key -- the caller (<c>HostKeyPromptWindow</c>) still
    /// lets the user rename it, and rejects it outright if it collides with an existing host.</summary>
    public static string Derive(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var stem = Path.GetFileNameWithoutExtension(fileName);

        var cleaned = new StringBuilder(stem.Length);
        var lastWasSeparator = true; // suppresses a leading hyphen

        foreach (var ch in stem)
        {
            if (ch < 128 && char.IsLetterOrDigit(ch))
            {
                cleaned.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                cleaned.Append('-');
                lastWasSeparator = true;
            }
        }

        var result = cleaned.ToString().Trim('-');
        return result.Length > 0 ? result : Fallback;
    }
}
