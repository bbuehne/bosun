using Bosun.Terminal;

namespace Bosun.Tests.Terminal.Fakes;

/// <summary>
/// In-memory <see cref="IFragmentFileSystem"/>. Models exactly the operations
/// <see cref="FragmentWriter"/> uses, so tests can simulate a failure at any step of the
/// temp-file-then-move sequence and assert what survives -- without ever touching a real path
/// (CLAUDE.md worktree-safety rules).
/// </summary>
internal sealed class FakeFragmentFileSystem : IFragmentFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path any method on this fake was ever called with, in call order -- used to
    /// assert the writer never opens a path containing <c>settings.json</c>.</summary>
    public List<string> TouchedPaths { get; } = [];

    /// <summary>When set, <see cref="Move"/> throws this instead of moving -- simulates a failure
    /// between the temp file being written and it becoming the real fragment.</summary>
    public Exception? FailOnMove { get; set; }

    /// <summary>When set, <see cref="WriteAllTextAsync"/> throws this instead of writing.</summary>
    public Exception? FailOnWrite { get; set; }

    public string? DestinationContent(string path) => _files.GetValueOrDefault(path);

    /// <summary>Seeds a "previous fragment" at <paramref name="path"/>, as if a prior successful
    /// write had already happened.</summary>
    public void SeedExistingFragment(string path, string contents) => _files[path] = contents;

    public void CreateDirectory(string path)
    {
        TouchedPaths.Add(path);
        _directories.Add(path);
    }

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        TouchedPaths.Add(path);
        if (FailOnWrite is not null)
        {
            throw FailOnWrite;
        }

        _files[path] = contents;
        return Task.CompletedTask;
    }

    public void Move(string sourcePath, string destinationPath)
    {
        TouchedPaths.Add(sourcePath);
        TouchedPaths.Add(destinationPath);

        if (FailOnMove is not null)
        {
            throw FailOnMove;
        }

        if (!_files.TryGetValue(sourcePath, out var contents))
        {
            throw new FileNotFoundException($"Fake has no file at '{sourcePath}'.");
        }

        _files[destinationPath] = contents;
        _files.Remove(sourcePath);
    }

    public bool FileExists(string path)
    {
        TouchedPaths.Add(path);
        return _files.ContainsKey(path);
    }

    public void DeleteFile(string path)
    {
        TouchedPaths.Add(path);
        _files.Remove(path);
    }
}
