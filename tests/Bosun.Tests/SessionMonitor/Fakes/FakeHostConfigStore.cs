using Bosun.Configuration;

namespace Bosun.Tests.SessionMonitor.Fakes;

/// <summary>Minimal fake <see cref="IHostConfigStore"/> -- just enough to hand
/// <see cref="Bosun.SessionMonitor.SshSessionMonitor"/> a fixed <see cref="Current"/> to correlate
/// against. Reload/watch behaviour is <c>HostConfigStore</c>'s concern (bs-30b), not this
/// component's, so it is not modelled here.</summary>
internal sealed class FakeHostConfigStore(BosunConfig config) : IHostConfigStore
{
    public BosunConfig Current { get; } = config;

#pragma warning disable CS0067 // never raised -- this fake never reloads
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067
}
