using System.IO;

namespace Bosun.Terminal;

/// <summary>Real-filesystem <see cref="IFragmentFileSystem"/>. Production only -- tests always
/// inject a fake (CLAUDE.md worktree-safety rules).</summary>
public sealed class RealFragmentFileSystem : IFragmentFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: true);

    public bool FileExists(string path) => File.Exists(path);

    public void DeleteFile(string path) => File.Delete(path);
}
