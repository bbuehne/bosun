using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// Drives a <see cref="MountSupervisor"/> against <see cref="ProbeDouble"/>/<see cref="RcloneDouble"/>
/// and an injected <see cref="FakeTimeProvider"/>. Same pump strategy as the implementer's harness
/// (enqueue, then drain the supervisor's channel) because that is the only deterministic way to
/// observe a channel-serialised state machine -- but wired to the doubles in this folder so these
/// tests do not inherit any assumption baked into the implementer's fakes.
/// </summary>
/// <remarks>
/// Nothing here can reach a real drive letter, a real <c>rclone rcd</c>, WinFsp, a real SFTP host,
/// or the real Windows Terminal fragment path: the only collaborators are two in-memory doubles, a
/// fixed in-memory config, and a fake clock. No sleeps and no wall clock -- time only moves when a
/// test says <see cref="AdvanceAsync"/>.
/// </remarks>
internal sealed class IndependentHarness
{
    public IndependentHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new ProbeDouble(Log);
        Rclone = new RcloneDouble(Log);
        Supervisor = new MountSupervisor(
            new FakeHostConfigStore(config), Rclone, Probe, Time, NullLogger<MountSupervisor>.Instance);
    }

    public FakeTimeProvider Time { get; }

    public SupervisorCallLog Log { get; } = new();

    public ProbeDouble Probe { get; }

    public RcloneDouble Rclone { get; }

    public MountSupervisor Supervisor { get; }

    /// <summary>Runs <paramref name="operation"/> to completion, including every follow-up action
    /// it chains onto the supervisor's channel.</summary>
    public async Task RunAsync(Func<Task> operation)
    {
        var task = operation();
        await Supervisor.DrainAsync();
        await task;
    }

    public Task StartAsync() => RunAsync(() => Supervisor.StartAsync());

    /// <summary>Moves the fake clock forward by <paramref name="delta"/>, firing every timer due
    /// within the window, then runs everything those timers enqueued.</summary>
    public async Task AdvanceAsync(TimeSpan delta)
    {
        Time.Advance(delta);
        await Supervisor.DrainAsync();
    }

    public Task AdvanceSecondsAsync(int seconds) => AdvanceAsync(TimeSpan.FromSeconds(seconds));

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    /// <summary>The hostname <c>HostFixtures</c> derives from a host key -- what
    /// <see cref="ProbeDouble.ShallowCount"/> is keyed by.</summary>
    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";

    public int ShallowProbesFor(string hostKey) => Probe.ShallowCount(HostnameOf(hostKey));
}
