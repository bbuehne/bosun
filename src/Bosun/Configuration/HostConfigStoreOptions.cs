namespace Bosun.Configuration;

/// <summary>Tunables for <see cref="HostConfigStore"/>'s debounce/retry behaviour. Defaults are
/// production-sane; tests inject a <see cref="TimeProvider"/> fake rather than shrinking these,
/// so the values here never need to change for testability.</summary>
public sealed record HostConfigStoreOptions
{
    /// <summary>How long to wait after the LAST raw <see cref="IConfigFileWatcher.Changed"/>
    /// signal before attempting a reload. Coalesces the burst of events one editor save often
    /// produces into a single attempt.</summary>
    public TimeSpan DebounceDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Delay between read retries once a reload attempt sees a transient failure
    /// (locked file, or empty content from a truncate-then-write race).</summary>
    public TimeSpan ReadRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Total read attempts (the first plus retries) before a transient failure is
    /// surfaced via <see cref="IHostConfigStore.ConfigReloadFailed"/> without evicting the last
    /// valid config.</summary>
    public int MaxReadAttempts { get; init; } = 3;
}
