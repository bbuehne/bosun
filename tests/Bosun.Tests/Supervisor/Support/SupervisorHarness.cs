using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor.Support;

/// <summary>
/// Wires a <see cref="MountSupervisor"/> to fakes + <see cref="FakeTimeProvider"/> and gives tests
/// a deterministic way to drive it: every stimulus (a command, or advancing the fake clock) is
/// followed by draining the supervisor's internal channel (<c>MountSupervisor.DrainAsync</c>) so
/// assertions never race the state machine -- no sleeps, no real clock, matching CLAUDE.md's
/// worktree-safety rules and the synchronous-continuation testing style already established by
/// <c>HostProbe</c>/<c>RcloneProcessService</c> tests.
/// </summary>
internal sealed class SupervisorHarness
{
    public FakeTimeProvider Time { get; }

    public FakeProbe Probe { get; } = new();

    public FakeRcloneMountClient Rclone { get; } = new();

    public MountSupervisor Supervisor { get; }

    public SupervisorHarness(BosunConfig config, DateTimeOffset? start = null)
    {
        Time = new FakeTimeProvider(start);
        var configStore = new FakeHostConfigStore(config);
        Supervisor = new MountSupervisor(configStore, Rclone, Probe, Time, NullLogger<MountSupervisor>.Instance);
    }

    /// <summary>Enqueues <paramref name="operation"/>, drains the channel until the resulting work
    /// (and everything it chains into) has run, then awaits the operation's own task so any
    /// exception surfaces to the caller.</summary>
    public async Task RunAsync(Func<Task> operation)
    {
        var task = operation();
        await Supervisor.DrainAsync();
        await task;
    }

    public Task StartAsync() => RunAsync(() => Supervisor.StartAsync());

    /// <summary>Advances the fake clock (firing every timer due within the advance, per
    /// <see cref="FakeTimeProvider.Advance"/>) then drains the channel so every action those
    /// timers enqueued -- and anything IT chains into -- has actually run before returning.</summary>
    public async Task AdvanceAsync(TimeSpan delta)
    {
        Time.Advance(delta);
        await Supervisor.DrainAsync();
    }

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);
}
