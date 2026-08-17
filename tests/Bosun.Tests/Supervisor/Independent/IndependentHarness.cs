using System.Reflection;
using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
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
/// fake clock, and an in-memory config store. No sleeps and no wall clock -- time only moves when a
/// test says <see cref="AdvanceAsync"/>.
/// </remarks>
internal sealed class IndependentHarness
{
    public IndependentHarness(BosunConfig config)
    {
        Time = new FakeTimeProvider();
        Probe = new ProbeDouble(Log);
        Rclone = new RcloneDouble(Log);
        Store = new ReloadableConfigStoreDouble(config);
        Supervisor = new MountSupervisor(
            Store, Rclone, Probe, Time, NullLogger<MountSupervisor>.Instance);
    }

    public FakeTimeProvider Time { get; }

    public SupervisorCallLog Log { get; } = new();

    public ProbeDouble Probe { get; }

    public RcloneDouble Rclone { get; }

    public ReloadableConfigStoreDouble Store { get; }

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

    /// <summary>
    /// A successful config reload (bs-7ck): <paramref name="next"/> becomes the store's
    /// <c>Current</c>, the store raises <c>ConfigChanged</c>, and everything the supervisor
    /// enqueues in response runs to completion before this returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// bs-7ck's DESIGN pins the semantics ("config change arrives as a supervisor command, posted
    /// to the same serialised channel") but not the shape of the seam, and these tests were written
    /// alongside the implementation rather than after it. Two shapes are therefore accepted, and
    /// only one of them ever fires:
    /// </para>
    /// <list type="number">
    /// <item>the supervisor subscribes to <see cref="IHostConfigStore.ConfigChanged"/> itself --
    /// the shape the issue's DESCRIPTION names ("does not subscribe to
    /// <c>IHostConfigStore.ConfigChanged</c>"). Publishing is then the whole trigger and nothing
    /// below runs;</item>
    /// <item>the supervisor exposes an explicit command seam and composition-root wiring does the
    /// subscribing -- the shape <c>OnRcloneRestartedAsync</c> already uses. With no subscriber on
    /// the store, this stands in for that wiring.</item>
    /// </list>
    /// <para>
    /// The reflection is confined to this method on purpose: no test below knows or cares which
    /// shape shipped, so none of them needs rewriting when it is settled. If neither shape exists,
    /// every config-reload test fails with the message below rather than silently passing because
    /// the reload went nowhere -- which is the failure mode that matters most here.
    /// </para>
    /// </remarks>
    public async Task ReloadAsync(BosunConfig next)
    {
        Store.Publish(next);

        if (!Store.LastPublishHadSubscribers)
        {
            // Must go through RunAsync, NOT `await seam; await DrainAsync();`.
            //
            // The seam is EnqueueAndWait: it writes to the supervisor's channel and returns a task
            // that completes only once that item is PUMPED. In tests the pump is DrainAsync, which
            // nothing calls automatically. Awaiting the seam first therefore waits forever for a
            // pump that is on the next line and can never be reached -- the whole suite hangs with
            // no failing test, which is how this was found. RunAsync starts the operation, pumps,
            // and only then awaits, which is why every other harness entry point uses it.
            //
            // Production is unaffected: RunAsync (the real loop) pumps continuously.
            await RunAsync(() => InvokeExplicitReloadSeamAsync(next));
            return;
        }

        await Supervisor.DrainAsync();
    }

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

    public bool IsSupervised(string hostKey) =>
        Supervisor.GetSnapshot().Any(h => h.HostKey == hostKey);

    /// <summary>The hostname <c>HostFixtures</c> derives from a host key -- what
    /// <see cref="ProbeDouble.ShallowCount"/> is keyed by.</summary>
    public static string HostnameOf(string hostKey) => $"{hostKey}.example.internal";

    public int ShallowProbesFor(string hostKey) => Probe.ShallowCount(HostnameOf(hostKey));

    private async Task InvokeExplicitReloadSeamAsync(BosunConfig next)
    {
        var seam = typeof(MountSupervisor)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name.Contains("Config", StringComparison.Ordinal) &&
                m.Name.EndsWith("Async", StringComparison.Ordinal) &&
                typeof(Task).IsAssignableFrom(m.ReturnType));

        if (seam is null)
        {
            throw new InvalidOperationException(
                "bs-7ck: this build of MountSupervisor neither subscribes to " +
                "IHostConfigStore.ConfigChanged nor exposes a public *Config*Async command seam, " +
                "so a config save reaches the supervisor by no route at all. Adding a host in the " +
                "window therefore still needs an app restart, and the new host is missing from " +
                "StatusReadModel until then.");
        }

        var arguments = seam.GetParameters()
            .Select(p => p.ParameterType == typeof(BosunConfig) ? next
                : p.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                : p.HasDefaultValue ? p.DefaultValue
                : throw new InvalidOperationException(
                    $"bs-7ck: cannot call the config-reload seam {seam.Name}: unexpected parameter " +
                    $"'{p.Name}' of type {p.ParameterType}."))
            .ToArray();

        await (Task)seam.Invoke(Supervisor, arguments)!;
    }
}
