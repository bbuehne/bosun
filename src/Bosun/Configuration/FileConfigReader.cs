using System.IO;

namespace Bosun.Configuration;

/// <summary>Real-filesystem <see cref="IConfigFileReader"/>. Production only — tests always
/// inject a fake (CLAUDE.md worktree-safety rules; bs-30b).</summary>
public sealed class FileConfigReader : IConfigFileReader
{
    public string ReadAllText(string path) => File.ReadAllText(path);
}
