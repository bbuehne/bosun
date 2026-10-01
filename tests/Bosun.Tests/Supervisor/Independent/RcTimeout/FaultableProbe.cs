using Bosun.Probe;
using Bosun.Tests.Supervisor.Independent.Fakes;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>
/// Wraps <see cref="ProbeDouble"/> so a deep probe can throw instead of returning an outcome.
/// </summary>
/// <remarks>
/// <see cref="IProbe"/> reports failure through its result type. The real <c>HostProbe</c> is not
/// expected to throw. The ability to throw is here for one purpose only: to make a timer-driven
/// supervisor action fail with an exception the test chooses. That lets the
/// <c>RunAsync</c> tests check that the loop survives an action throwing, whatever the action
/// is, independently of the rc call sites. No test here asserts what STATE a throwing probe
/// should leave a host in, because the spec does not say.
/// </remarks>
internal sealed class FaultableProbe(ProbeDouble scripted) : IProbe
{
    private readonly object gate = new();
    private readonly Dictionary<string, (Func<Exception> Factory, int Remaining)> deepThrows = new(StringComparer.Ordinal);

    public ProbeDouble Scripted { get; } = scripted;

    /// <summary>Called with the host key immediately before a scripted deep-probe throw.</summary>
    public Action<string>? OnDeepThrow { get; set; }

    public void ThrowOnDeep(string hostKey, Func<Exception> fault, int times)
    {
        lock (gate)
        {
            deepThrows[hostKey] = (fault, times);
        }
    }

    public Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
        Scripted.ProbeShallowAsync(hostname, port, timeout, cancellationToken);

    public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Func<Exception>? factory = null;
        lock (gate)
        {
            if (deepThrows.TryGetValue(hostKey, out var entry) && entry.Remaining > 0)
            {
                deepThrows[hostKey] = (entry.Factory, entry.Remaining - 1);
                factory = entry.Factory;
            }
        }

        if (factory is not null)
        {
            OnDeepThrow?.Invoke(hostKey);
            return Task.FromException<DeepProbeResult>(factory());
        }

        return Scripted.ProbeDeepAsync(hostKey, timeout, cancellationToken);
    }
}
