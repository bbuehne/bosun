using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Bosun.Probe;

/// <summary>
/// Real <see cref="IProbe"/> (bs-pk4, bs-k8p). Shallow probing composes
/// <see cref="ITcpProbeTransport"/>; deep probing composes <see cref="IRemoteRootLister"/>. Both
/// methods enforce their timeout the same way: a <see cref="TimeProvider"/>-driven timer cancels
/// a linked token if the underlying call has not completed in time, so timeout behaviour is
/// deterministic and testable without a real wall-clock delay (CLAUDE.md worktree safety — no
/// <c>Thread.Sleep</c>, no real clock).
/// </summary>
public sealed class HostProbe(
    ITcpProbeTransport tcpTransport,
    IRemoteRootLister remoteRootLister,
    TimeProvider timeProvider,
    ILogger<HostProbe> logger) : IProbe
{
    public async Task<ShallowProbeResult> ProbeShallowAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = timeProvider.GetUtcNow();

        using var timeoutSource = new CancellationTokenSource();
        using var timer = timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource, timeout, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await tcpTransport.ConnectAsync(hostname, port, linked.Token).ConfigureAwait(false);
            return BuildShallowResult(ShallowProbeOutcome.Success, start, detail: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BuildShallowResult(ShallowProbeOutcome.Cancelled, start, "Probe was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return BuildShallowResult(ShallowProbeOutcome.Timeout, start, $"No response within {timeout.TotalSeconds:0.#}s.");
        }
        catch (SocketException ex) when (IsDnsFailure(ex))
        {
            return BuildShallowResult(ShallowProbeOutcome.DnsFailure, start, ex.Message);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return BuildShallowResult(ShallowProbeOutcome.ConnectionRefused, start, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Shallow probe to {Hostname}:{Port} failed", hostname, port);
            return BuildShallowResult(ShallowProbeOutcome.OtherFailure, start, ex.Message);
        }
    }

    public async Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = timeProvider.GetUtcNow();

        using var timeoutSource = new CancellationTokenSource();
        using var timer = timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource, timeout, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await remoteRootLister.ListRootAsync(hostKey, linked.Token).ConfigureAwait(false);
            return BuildDeepResult(DeepProbeOutcome.Success, start, detail: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BuildDeepResult(DeepProbeOutcome.Cancelled, start, "Probe was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return BuildDeepResult(DeepProbeOutcome.Timeout, start, $"No response within {timeout.TotalSeconds:0.#}s.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Deep probe for host {HostKey} failed", hostKey);
            return BuildDeepResult(DeepProbeOutcome.Failed, start, ex.Message);
        }
    }

    private ShallowProbeResult BuildShallowResult(ShallowProbeOutcome outcome, DateTimeOffset start, string? detail) => new()
    {
        Outcome = outcome,
        Elapsed = timeProvider.GetUtcNow() - start,
        Detail = detail,
    };

    private DeepProbeResult BuildDeepResult(DeepProbeOutcome outcome, DateTimeOffset start, string? detail) => new()
    {
        Outcome = outcome,
        Elapsed = timeProvider.GetUtcNow() - start,
        Detail = detail,
    };

    /// <summary>Which <see cref="SocketError"/> values represent DNS resolution failure, as
    /// distinct from a successful resolution followed by a refused/failed connection.</summary>
    private static bool IsDnsFailure(SocketException ex) =>
        ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain;
}
