using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// Drives a <see cref="MountSupervisor"/> for the bs-obf independent tests (docs/ARCHITECTURE.md §4
/// rule 7: the idle unmount follows rclone's VFS activity). Same pump strategy as the other
/// independent harnesses: enqueue, then drain the channel; the fake clock moves only when a test
/// says so, one second at a time so that every timer the supervisor re-arms inside an action is
/// seen at its own due time.
/// </summary>
/// <remarks>
/// Nothing here reaches a real drive letter, <c>rclone rcd</c>, WinFsp, a real host or the real
/// fragment path: the collaborators are <see cref="ProbeDouble"/>, <see cref="VfsScriptedRclone"/>,
/// an in-memory config store and a <see cref="FakeTimeProvider"/>.
/// </remarks>
internal sealed class IdleActivityHarness
{
    /// <summary>Wall-clock guard for the few tests that have to wait for another thread. Only ever
    /// reached when the supervisor is stuck; a passing run never waits on it.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    public IdleActivityHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new ProbeDouble(Log);
        Rclone = new VfsScriptedRclone(Time, Log);
        Store = new ReloadableConfigStoreDouble(config);
        Supervisor = new MountSupervisor(
            Store, Rclone, Probe, new Bosun.Tests.Supervisor.Fakes.FakeDriveTypeReader(), Time,
            NullLogger<MountSupervisor>.Instance);
    }

    public FakeTimeProvider Time { get; }

    public SupervisorCallLog Log { get; } = new();

    public ProbeDouble Probe { get; }

    public VfsScriptedRclone Rclone { get; }

    public ReloadableConfigStoreDouble Store { get; }

    public MountSupervisor Supervisor { get; }

    /// <summary>Seconds since the fake clock started (the supervisor starts at 0).</summary>
    public double Now => (Time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;

    /// <summary>An on-demand host with an idle timeout, and the shipped mounted-probe cadence (60 s)
    /// unless told otherwise. <c>interval_seconds = 0</c>, the recommended on-demand setting.</summary>
    public static IdleActivityHarness OnDemand(
        int idleUnmountSeconds,
        int mountedProbeIntervalSeconds = 60,
        int probeTimeoutSeconds = 5,
        int mountedDeepProbeIntervalSeconds = 300,
        string key = "archive",
        string drive = "Q:")
    {
        var host = HostFixtures.OnDemand(key, probeIntervalSeconds: 0, idleUnmountSeconds: idleUnmountSeconds, drive: drive);
        var global = HostFixtures.Global(
            probeTimeoutSeconds: probeTimeoutSeconds,
            mountedProbeIntervalSeconds: mountedProbeIntervalSeconds,
            mountedDeepProbeIntervalSeconds: mountedDeepProbeIntervalSeconds);
        return new IdleActivityHarness(HostFixtures.Build(global, host));
    }

    public async Task RunAsync(Func<Task> operation)
    {
        var task = operation();
        await Supervisor.DrainAsync();
        await task;
    }

    public Task StartAsync() => RunAsync(() => Supervisor.StartAsync());

    public Task MountAsync(string hostKey) => RunAsync(() => Supervisor.RequestMountAsync(hostKey));

    public Task UnmountAsync(string hostKey) => RunAsync(() => Supervisor.RequestUnmountAsync(hostKey));

    public Task SuspendAsync() => RunAsync(() => Supervisor.SuspendAsync());

    public Task ResumeAsync() => RunAsync(() => Supervisor.ResumeAsync());

    /// <summary>A validated config save (bs-7ck), through whichever seam the supervisor has: its own
    /// subscription to the store, or the explicit <see cref="MountSupervisor.ConfigChangedAsync"/>.</summary>
    public async Task ReloadAsync(BosunConfig next)
    {
        Store.Publish(next);
        if (Store.LastPublishHadSubscribers)
        {
            await Supervisor.DrainAsync();
            return;
        }

        await RunAsync(() => Supervisor.ConfigChangedAsync(next));
    }

    /// <summary>Advances the clock in one-second steps, pumping after each.</summary>
    public async Task AdvanceSecondsAsync(double seconds)
    {
        var target = Time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        while (Time.GetUtcNow() < target)
        {
            var remaining = target - Time.GetUtcNow();
            Time.Advance(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
            await Supervisor.DrainAsync();
        }
    }

    /// <summary>Advances to <paramref name="secondsSinceStart"/> on the fake clock.</summary>
    public Task AdvanceToAsync(double secondsSinceStart) => AdvanceSecondsAsync(secondsSinceStart - Now);

    /// <summary>Advances one second at a time until <paramref name="condition"/> holds or
    /// <paramref name="maxSeconds"/> have passed; returns whether it held.</summary>
    public async Task<bool> AdvanceUntilAsync(Func<bool> condition, double maxSeconds)
    {
        var limit = Now + maxSeconds;
        while (Now < limit)
        {
            if (condition())
            {
                return true;
            }

            await AdvanceSecondsAsync(1);
        }

        return condition();
    }

    /// <summary>The hostname <c>HostFixtures</c> derives from a host key.</summary>
    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";

    public HostMountSnapshot Snapshot(string hostKey) =>
        Supervisor.GetSnapshot().Single(h => h.HostKey == hostKey);

    public MountState State(string hostKey) => Snapshot(hostKey).State;

    /// <summary><paramref name="hostKey"/>'s transitions, OLDEST FIRST.</summary>
    public IReadOnlyList<MountTransitionEntry> Transitions(string hostKey) =>
        Supervisor.GetTransitionHistory().Reverse().Where(t => t.HostKey == hostKey).ToList();

    public IReadOnlyList<MountTransitionEntry> DrainsWith(string hostKey, DrainCause cause) =>
        Transitions(hostKey).Where(t => t.To == MountState.Draining && t.DrainCause == cause).ToList();

    /// <summary>Seconds-since-start of the first transition into Draining with
    /// <paramref name="cause"/> at or after <paramref name="notBefore"/>, or <see langword="null"/>.</summary>
    public double? DrainTime(string hostKey, DrainCause cause, double notBefore = 0) =>
        DrainsWith(hostKey, cause)
            .Select(t => (t.TimestampUtc - DateTimeOffset.UnixEpoch).TotalSeconds)
            .Where(s => s >= notBefore)
            .Cast<double?>()
            .FirstOrDefault();

    public string Dump(string hostKey) =>
        $"clock +{Now}s{Environment.NewLine}" +
        string.Join(Environment.NewLine, Transitions(hostKey).Select(t =>
            $"  +{(t.TimestampUtc - DateTimeOffset.UnixEpoch).TotalSeconds}s {t.From} -> {t.To} ({t.Trigger}; cause {t.DrainCause?.ToString() ?? "-"})")) +
        $"{Environment.NewLine}vfs calls: {Rclone.VfsCallCount}; mount/mount: {Rclone.MountRequests.Count}; mount/unmount: {Rclone.UnmountRequests.Count}";
}
