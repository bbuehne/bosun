using System.IO;

namespace Bosun.Configuration;

/// <summary>
/// Abstracts "something happened to the config file" so <see cref="HostConfigStore"/> can be
/// driven deterministically in tests instead of depending on real <see cref="FileSystemWatcher"/>
/// timing (CLAUDE.md: "No Thread.Sleep, no reliance on real FileSystemWatcher timing in unit
/// tests").
/// </summary>
public interface IConfigFileWatcher : IDisposable
{
    /// <summary>
    /// Raised whenever the underlying file may have changed. Deliberately raw and un-debounced —
    /// editors write in bursts, so this can fire several times for one logical save.
    /// <see cref="HostConfigStore"/> is what coalesces it.
    /// </summary>
    event Action? Changed;
}
