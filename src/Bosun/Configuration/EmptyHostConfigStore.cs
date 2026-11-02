namespace Bosun.Configuration;

/// <summary>
/// A zero-host <see cref="IHostConfigStore"/> for the UI to stand on when the real one cannot load
/// (bs-4js). <c>HostConfigStore.Load</c> throws on an invalid first load by design (ADR-012), and
/// that throw used to take the whole UI with it -- the tray icon that ADR-012 Decision 2 promises
/// for "config invalid" never existed. Not registered in the container and never given to the
/// supervisor: it exists only so the tray and window can be built and can report the real cause,
/// which reaches them through the app-health model (<c>startup.config-invalid</c>), not through here.
/// </summary>
/// <remarks>Never raises either event: nothing about an empty, fixed config changes. Defaults are
/// <see cref="GlobalConfig"/>'s own.</remarks>
public sealed class EmptyHostConfigStore : IHostConfigStore
{
    public BosunConfig Current { get; } = new()
    {
        Global = new GlobalConfig(),
        Hosts = new Dictionary<string, HostConfig>(),
    };

#pragma warning disable CS0067 // never raised -- an empty fixed config never reloads
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067
}
