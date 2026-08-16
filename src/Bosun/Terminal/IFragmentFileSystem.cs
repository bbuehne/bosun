namespace Bosun.Terminal;

/// <summary>
/// The narrow filesystem seam <see cref="FragmentWriter"/> uses for its atomic-write sequence.
/// Exists so tests can fake a failure partway through that sequence (e.g. the move step) and assert
/// the previous fragment survives, without any test touching a real file (bs-k41 acceptance:
/// "a simulated mid-write failure leaves the previous fragment intact").
/// </summary>
public interface IFragmentFileSystem
{
    void CreateDirectory(string path);

    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken);

    /// <summary>Moves <paramref name="sourcePath"/> to <paramref name="destinationPath"/>,
    /// overwriting any existing file there. This is the operation that makes the write atomic from
    /// an outside observer's point of view -- everything before it only touches the temp file.</summary>
    void Move(string sourcePath, string destinationPath);

    bool FileExists(string path);

    void DeleteFile(string path);
}
