using System.IO;

namespace Bosun.Configuration;

/// <summary>
/// Real <see cref="System.IO.FileSystemWatcher"/>-backed <see cref="IConfigFileWatcher"/>.
/// Production only — tests always inject a fake and drive it manually (bs-30b).
/// </summary>
/// <remarks>
/// Watches the containing directory rather than the file directly and reacts to
/// <c>Changed</c>, <c>Created</c>, and <c>Renamed</c>: editors commonly save by writing a
/// temp file and renaming it over the original, which a watcher scoped only to "Changed on this
/// exact file" can miss entirely.
/// </remarks>
public sealed class FileSystemConfigWatcher : IConfigFileWatcher
{
    private readonly FileSystemWatcher _watcher;

    public event Action? Changed;

    public FileSystemConfigWatcher(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no containing directory.", nameof(path));
        var fileName = Path.GetFileName(fullPath);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnRaised;
        _watcher.Created += OnRaised;
        _watcher.Renamed += OnRaised;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnRaised(object sender, FileSystemEventArgs e) => Changed?.Invoke();

    private void OnError(object sender, ErrorEventArgs e) =>
        // A dropped/overflowed watcher is itself a "something changed, go check" signal — we
        // have no diff to react to, so treat it the same as a raw change notification and let
        // HostConfigStore's normal read-and-validate path sort out whether anything is different.
        Changed?.Invoke();

    public void Dispose()
    {
        _watcher.Changed -= OnRaised;
        _watcher.Created -= OnRaised;
        _watcher.Renamed -= OnRaised;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
