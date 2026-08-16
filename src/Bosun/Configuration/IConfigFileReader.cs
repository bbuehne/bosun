using System.IO;

namespace Bosun.Configuration;

/// <summary>
/// Abstracts reading <c>hosts.toml</c> off disk, so <see cref="HostConfigStore"/> can be tested
/// without ever touching a real file (CLAUDE.md worktree-safety rules).
/// </summary>
public interface IConfigFileReader
{
    /// <summary>
    /// Reads the full text of the file at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="IOException">
    /// The file could not be read right now — locked by another process, or briefly absent
    /// mid-save. Callers (see <see cref="HostConfigStore"/>) treat this as transient and retry
    /// briefly; it is not, by itself, an invalid config.
    /// </exception>
    string ReadAllText(string path);
}
