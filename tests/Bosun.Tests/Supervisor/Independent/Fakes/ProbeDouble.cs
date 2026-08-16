using Bosun.Probe;

namespace Bosun.Tests.Supervisor.Independent.Fakes;

/// <summary>
/// An <see cref="IProbe"/> double written independently of the implementer's <c>FakeProbe</c>.
/// Two capabilities it adds, both needed by tests in this folder:
/// </summary>
/// <remarks>
/// <para>
/// <b>Exact per-host call counts.</b> Cadence claims (ADR-011) are about how MANY probes happen
/// and WHEN, not merely that state eventually changes. Counting per hostname makes "advancing to
/// one second short of the interval produces exactly zero probes" expressible.
/// </para>
/// <para>
/// <b>Bounded failure scripting.</b> <see cref="SetDeep"/> and <see cref="SetShallow"/> take a
/// <c>times</c> budget. A test that wants to prove the supervisor does not retry a permanently
/// failing operation without bound needs the failure to eventually stop -- otherwise a genuine
/// unbounded-retry bug manifests as a hung test host or a StackOverflowException that takes the
/// whole run down, rather than as a readable assertion failure with a call count in it.
/// </para>
/// <para>
/// The recorded timeout is also asserted on: a probe issued with no timeout would hang the
/// single-threaded supervisor loop (docs/ARCHITECTURE.md §5), which is a wedge, not a slowdown.
/// </para>
/// </remarks>
internal sealed class ProbeDouble(SupervisorCallLog log) : IProbe
{
    private readonly Dictionary<string, ScriptedOutcome<ShallowProbeOutcome>> shallowScript =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ScriptedOutcome<DeepProbeOutcome>> deepScript =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> shallowCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> deepCounts = new(StringComparer.Ordinal);

    public ShallowProbeOutcome DefaultShallow { get; set; } = ShallowProbeOutcome.Success;

    public DeepProbeOutcome DefaultDeep { get; set; } = DeepProbeOutcome.Success;

    public TimeSpan? LastShallowTimeout { get; private set; }

    public TimeSpan? LastDeepTimeout { get; private set; }

    public int TotalShallowCalls { get; private set; }

    public int TotalDeepCalls { get; private set; }

    /// <summary>Scripts <paramref name="hostname"/>'s shallow outcome. <paramref name="times"/>
    /// bounds how many calls the scripted outcome applies to; after that the host falls back to
    /// <see cref="DefaultShallow"/>. <c>int.MaxValue</c> (the default) means "sticky".</summary>
    public void SetShallow(string hostname, ShallowProbeOutcome outcome, int times = int.MaxValue) =>
        shallowScript[hostname] = new ScriptedOutcome<ShallowProbeOutcome>(outcome, times);

    /// <summary>Scripts <paramref name="hostKey"/>'s deep outcome; see <see cref="SetShallow"/>
    /// for <paramref name="times"/>.</summary>
    public void SetDeep(string hostKey, DeepProbeOutcome outcome, int times = int.MaxValue) =>
        deepScript[hostKey] = new ScriptedOutcome<DeepProbeOutcome>(outcome, times);

    public int ShallowCount(string hostname) =>
        shallowCounts.TryGetValue(hostname, out var n) ? n : 0;

    public int DeepCount(string hostKey) => deepCounts.TryGetValue(hostKey, out var n) ? n : 0;

    public Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        LastShallowTimeout = timeout;
        TotalShallowCalls++;
        shallowCounts[hostname] = ShallowCount(hostname) + 1;

        var outcome = DefaultShallow;
        if (shallowScript.TryGetValue(hostname, out var scripted) && scripted.TryConsume(out var scriptedOutcome))
        {
            outcome = scriptedOutcome;
        }

        log.Record($"shallow|{hostname}|{outcome}");

        return Task.FromResult(new ShallowProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == ShallowProbeOutcome.Success ? null : $"scripted {outcome}",
        });
    }

    public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        LastDeepTimeout = timeout;
        TotalDeepCalls++;
        deepCounts[hostKey] = DeepCount(hostKey) + 1;

        var outcome = DefaultDeep;
        if (deepScript.TryGetValue(hostKey, out var scripted) && scripted.TryConsume(out var scriptedOutcome))
        {
            outcome = scriptedOutcome;
        }

        log.Record($"deep|{hostKey}|{outcome}");

        return Task.FromResult(new DeepProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == DeepProbeOutcome.Success ? null : $"scripted {outcome}",
        });
    }

    private sealed class ScriptedOutcome<T>(T outcome, int remaining)
        where T : struct
    {
        private int remainingCalls = remaining;

        public bool TryConsume(out T value)
        {
            value = outcome;
            if (remainingCalls <= 0)
            {
                return false;
            }

            if (remainingCalls != int.MaxValue)
            {
                remainingCalls--;
            }

            return true;
        }
    }
}
