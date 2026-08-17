using Bosun.Configuration;

namespace Bosun.Tests.UI.HostEditor.Fakes;

/// <summary>Minimal <see cref="IHostConfigStore"/> test double -- a fixed snapshot, no reload
/// behaviour (these tests never need it).</summary>
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
