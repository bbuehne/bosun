using Bosun.Configuration;

namespace Bosun.Tests.SystemEventIntegration.Independent.Fakes;

/// <summary>
/// An <see cref="IHostConfigStore"/> whose <see cref="Current"/> can be swapped after construction.
/// <c>FakeHostConfigStore</c> fixes it for the object's lifetime, which cannot express the thing
/// this folder needs to test: <c>global.suspend_unmount_timeout_seconds</c> is Invariant I8's budget,
/// and a config reload that lowers it must take effect on the very next suspend rather than on the
/// next application restart. See <c>SuspendBudgetAdversarialTests</c>.
/// </summary>
internal sealed class MutableConfigStore(BosunConfig config) : IHostConfigStore
{
    public BosunConfig Current { get; set; } = config;

#pragma warning disable CS0067 // Deliberately never raised: this store models a swap, not a watcher.
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067
}
