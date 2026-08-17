using Bosun.Configuration;

namespace Bosun.Tests.UI.Tray.Fakes;

internal sealed class FakeHostConfigStore : IHostConfigStore
{
    public FakeHostConfigStore(BosunConfig config)
    {
        Current = config;
    }

    public BosunConfig Current { get; }

#pragma warning disable CS0067 // never raised -- these tests never reload config.
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067
}
