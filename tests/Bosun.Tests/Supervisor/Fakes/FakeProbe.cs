using Bosun.Probe;

namespace Bosun.Tests.Supervisor.Fakes;

/// <summary>
/// Scriptable <see cref="IProbe"/> for <c>MountSupervisor</c> tests. Defaults to success for both
/// shallow and deep probes on every host so a test only has to script the specific
/// hosts/outcomes it cares about. Every call is recorded (by hostname for shallow, by host key
/// for deep) so tests can assert not just outcomes but which probe kind ran and how many times --
/// e.g. proving a shallow-probe failure never authorises a mount (Invariant I1), or that a
/// mounted host with <c>interval_seconds = 0</c> is still probed (ADR-011).
/// </summary>
internal sealed class FakeProbe : IProbe
{
    private readonly Dictionary<string, Queue<ShallowProbeResult>> shallowScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DeepProbeResult>> deepScripts = new(StringComparer.OrdinalIgnoreCase);

    private ShallowProbeOutcome defaultShallowOutcome = ShallowProbeOutcome.Success;
    private DeepProbeOutcome defaultDeepOutcome = DeepProbeOutcome.Success;

    public List<string> ShallowProbeCalls { get; } = [];
    public List<string> DeepProbeCalls { get; } = [];

    public void SetDefaultShallow(ShallowProbeOutcome outcome) => defaultShallowOutcome = outcome;

    public void SetDefaultDeep(DeepProbeOutcome outcome) => defaultDeepOutcome = outcome;

    public void EnqueueShallow(string hostname, ShallowProbeOutcome outcome)
    {
        if (!shallowScripts.TryGetValue(hostname, out var queue))
        {
            queue = new Queue<ShallowProbeResult>();
            shallowScripts[hostname] = queue;
        }

        queue.Enqueue(new ShallowProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == ShallowProbeOutcome.Success ? null : $"fake {outcome}",
        });
    }

    public void EnqueueDeep(string hostKey, DeepProbeOutcome outcome)
    {
        if (!deepScripts.TryGetValue(hostKey, out var queue))
        {
            queue = new Queue<DeepProbeResult>();
            deepScripts[hostKey] = queue;
        }

        queue.Enqueue(new DeepProbeResult
        {
            Outcome = outcome,
            Elapsed = TimeSpan.Zero,
            Detail = outcome == DeepProbeOutcome.Success ? null : $"fake {outcome}",
        });
    }

    public Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ShallowProbeCalls.Add(hostname);

        if (shallowScripts.TryGetValue(hostname, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        return Task.FromResult(new ShallowProbeResult
        {
            Outcome = defaultShallowOutcome,
            Elapsed = TimeSpan.Zero,
            Detail = defaultShallowOutcome == ShallowProbeOutcome.Success ? null : $"fake default {defaultShallowOutcome}",
        });
    }

    public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DeepProbeCalls.Add(hostKey);

        if (deepScripts.TryGetValue(hostKey, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        return Task.FromResult(new DeepProbeResult
        {
            Outcome = defaultDeepOutcome,
            Elapsed = TimeSpan.Zero,
            Detail = defaultDeepOutcome == DeepProbeOutcome.Success ? null : $"fake default {defaultDeepOutcome}",
        });
    }
}
