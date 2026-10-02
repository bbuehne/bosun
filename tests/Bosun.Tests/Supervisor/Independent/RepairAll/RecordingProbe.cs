using Bosun.Probe;
using Bosun.Tests.Supervisor.Independent.Fakes;

namespace Bosun.Tests.Supervisor.Independent.RepairAll;

/// <summary>
/// Wraps <see cref="ProbeDouble"/> (for its scripting) and writes every probe into the shared
/// <see cref="RepairEventLog"/>, so a probe can be ordered against the rc calls around it.
/// </summary>
internal sealed class RecordingProbe(ProbeDouble scripted, RepairEventLog log) : IProbe
{
    public ProbeDouble Scripted { get; } = scripted;

    public async Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await Scripted.ProbeShallowAsync(hostname, port, timeout, cancellationToken).ConfigureAwait(false);
        log.Record(RepairEventLog.Shallow, HostKeyOf(hostname), result.Outcome == ShallowProbeOutcome.Success);
        return result;
    }

    public async Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await Scripted.ProbeDeepAsync(hostKey, timeout, cancellationToken).ConfigureAwait(false);
        log.Record(RepairEventLog.Deep, hostKey, result.Outcome == DeepProbeOutcome.Success);
        return result;
    }

    /// <summary>Inverse of <c>HostFixtures</c>' <c>{key}.example.internal</c>, so both probe kinds
    /// are logged by host key.</summary>
    private static string HostKeyOf(string hostname)
    {
        const string suffix = ".example.internal";
        return hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? hostname[..^suffix.Length] : hostname;
    }
}
