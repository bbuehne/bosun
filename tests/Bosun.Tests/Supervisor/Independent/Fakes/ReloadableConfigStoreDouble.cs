using Bosun.Configuration;

namespace Bosun.Tests.Supervisor.Independent.Fakes;

/// <summary>
/// An <see cref="IHostConfigStore"/> double that can actually reload -- the one capability the
/// existing minimal fakes deliberately do not have (they all <c>#pragma warning disable CS0067</c>
/// their events because nothing ever raised them).
/// </summary>
/// <remarks>
/// <para>
/// It models exactly what <c>HostConfigStore</c> promises after a successful reload and nothing
/// more: <see cref="Current"/> is replaced <b>wholesale</b> (never edited in place, so a consumer
/// holding the old snapshot keeps seeing the old values), and only then is
/// <see cref="ConfigChanged"/> raised carrying the config that has already become
/// <see cref="Current"/>. Getting that order wrong in the double would let a supervisor bug --
/// reading <see cref="Current"/> from inside the handler and getting the stale config -- pass.
/// </para>
/// <para>
/// No file, no watcher, no timer: this is two fields and an event. Nothing here can reach
/// <c>config/hosts.toml</c> or any other real path.
/// </para>
/// </remarks>
internal sealed class ReloadableConfigStoreDouble(BosunConfig config) : IHostConfigStore
{
    public BosunConfig Current { get; private set; } = config;

    /// <summary>How many times <see cref="Publish"/> has been called.</summary>
    public int PublishCount { get; private set; }

    /// <summary>Whether anything was subscribed to <see cref="ConfigChanged"/> at the moment of
    /// the most recent <see cref="Publish"/>. Used by <c>IndependentHarness.ReloadAsync</c> to
    /// decide whether the event alone was enough to deliver the reload -- see its remarks.</summary>
    public bool LastPublishHadSubscribers { get; private set; }

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

#pragma warning disable CS0067 // No test in this folder exercises a rejected reload: an invalid
    // config never becomes Current (docs/ARCHITECTURE.md §6), so the supervisor never sees it.
    public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067

    /// <summary>A successful reload: <paramref name="next"/> becomes <see cref="Current"/>, then
    /// subscribers are told.</summary>
    public void Publish(BosunConfig next)
    {
        PublishCount++;
        Current = next;
        LastPublishHadSubscribers = ConfigChanged is not null;
        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs(next));
    }
}
