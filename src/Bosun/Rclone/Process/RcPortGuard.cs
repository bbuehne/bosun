using Microsoft.Extensions.Logging;

namespace Bosun.Rclone.Process;

/// <summary>Finds which process is listening on a loopback TCP port.</summary>
public interface IPortOwnerResolver
{
    /// <summary>The PID listening on <paramref name="port"/> (on loopback or any-address), or
    /// <see langword="null"/> when nothing is.</summary>
    int? GetListeningProcessId(int port);
}

/// <summary>Describes a process by PID. Returns <see langword="null"/> only when the process does
/// not exist (any more); fields the OS will not reveal come back null on the record.</summary>
public interface IProcessInspector
{
    ProcessDescription? Describe(int processId);
}

/// <summary>Terminates a specific process instance. The start time guards against PID reuse: the
/// PID is only honoured if it still belongs to the process that was inspected.</summary>
public interface IProcessTerminator
{
    /// <summary>Requests termination. <see langword="false"/> when it could not be done (access
    /// denied, process gone, PID now belongs to a different process).</summary>
    bool TryKill(int processId, DateTimeOffset expectedStartTime);

    /// <summary>True when that process instance no longer exists.</summary>
    bool HasExited(int processId, DateTimeOffset expectedStartTime);
}

public enum RcPortCheckOutcome
{
    /// <summary>Nothing is listening on the rc port (or we could not tell); go ahead and launch.</summary>
    Free,

    /// <summary>A stale rcd of Bosun's own was found, killed, and has exited; go ahead and launch.</summary>
    StaleRcdKilled,

    /// <summary>Something else holds the port and was left alone. Do not launch.</summary>
    HeldByOtherProcess,
}

public sealed record RcPortCheck(RcPortCheckOutcome Outcome, string? Message = null);

/// <summary>Checks the rc port before <c>rclone rcd</c> is launched (ADR-020 §2, bs-772).</summary>
public interface IRcPortGuard
{
    Task<RcPortCheck> EnsureFreeAsync(CancellationToken cancellationToken);

    /// <summary>One-line description of whatever holds the port right now, for a fault message
    /// after our own child failed to bind; <see langword="null"/> when nothing does.</summary>
    string? DescribeHolder();
}

/// <summary>
/// Implements ADR-020 §2. If the rc port is already held, either kill the holder (only when it
/// is provably a stale rcd of Bosun's own, per <see cref="StaleRcdMatcher"/>) or refuse and say
/// exactly who holds it. Never retries on its own: the caller's RestartDelay loop does, because
/// a legitimate holder may go away.
/// </summary>
/// <remarks>
/// Killing a stale rcd tears down any WinFsp mounts it held. That is consistent with I2 (an
/// unreachable-credential rcd is a mount Bosun cannot manage), and I1 plus startup crash
/// recovery bring them back only after a fresh probe. This class is NOT a second place that
/// calls <c>mount/mount</c> or <c>mount/unmount</c>; it terminates a process by PID and that is all.
/// </remarks>
public sealed class RcPortGuard(
    IPortOwnerResolver resolver,
    IProcessInspector inspector,
    IProcessTerminator terminator,
    RcloneProcessServiceOptions options,
    string currentUser,
    TimeProvider timeProvider,
    ILogger<RcPortGuard> logger) : IRcPortGuard
{
    public async Task<RcPortCheck> EnsureFreeAsync(CancellationToken cancellationToken)
    {
        int? holder;
        try
        {
            holder = resolver.GetListeningProcessId(options.RcloneRcPort);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Cannot tell. Launching is harmless: if the port IS held, our child exits on its
            // bind failure and the service reports that, naming the holder via DescribeHolder.
            logger.LogWarning(ex, "Could not determine who holds rc port {Port}; launching anyway", options.RcloneRcPort);
            return new RcPortCheck(RcPortCheckOutcome.Free);
        }

        if (holder is not { } pid)
        {
            return new RcPortCheck(RcPortCheckOutcome.Free);
        }

        var description = Describe(pid);
        if (description is null)
        {
            // Gone between the table read and the lookup.
            return new RcPortCheck(RcPortCheckOutcome.Free);
        }

        if (!StaleRcdMatcher.IsBosunsOwnRcd(description, options.RcloneRcPort, options.RcloneConfigPath, currentUser))
        {
            return Held(description);
        }

        var start = description.StartTime!.Value;
        logger.LogWarning(
            "rc port {Port} is held by a stale rclone rcd of Bosun's own (PID {ProcessId}, started {StartTime:o}, " +
            "image {ImagePath}); killing it. Any drives it held go away with it and are remounted after a fresh probe.",
            options.RcloneRcPort, pid, start, description.ImagePath);

        bool killRequested;
        try
        {
            killRequested = terminator.TryKill(pid, start);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Killing stale rclone rcd PID {ProcessId} failed", pid);
            killRequested = false;
        }

        if (!killRequested)
        {
            return Held(description, "it matched Bosun's own rcd but could not be terminated");
        }

        if (!await WaitForExitAsync(pid, start, cancellationToken).ConfigureAwait(false))
        {
            return Held(description, $"it was told to exit but is still running after {options.StaleKillTimeout}");
        }

        // The port should now be free; confirm rather than assume.
        int? after;
        try
        {
            after = resolver.GetListeningProcessId(options.RcloneRcPort);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not re-check rc port {Port} after killing the stale rcd", options.RcloneRcPort);
            return new RcPortCheck(RcPortCheckOutcome.StaleRcdKilled);
        }

        if (after is { } remaining && Describe(remaining) is { } still)
        {
            return Held(still);
        }

        return new RcPortCheck(RcPortCheckOutcome.StaleRcdKilled);
    }

    public string? DescribeHolder()
    {
        try
        {
            if (resolver.GetListeningProcessId(options.RcloneRcPort) is not { } pid)
            {
                return null;
            }

            return Describe(pid) is { } description
                ? $"port {options.RcloneRcPort} is held by {Who(description)}"
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    private RcPortCheck Held(ProcessDescription holder, string? why = null)
    {
        var message =
            $"Port {options.RcloneRcPort} is held by {Who(holder)}" +
            (why is null ? ", which is not a Bosun-launched rclone rcd, so Bosun will not kill it" : $"; {why}") +
            ". Stop that process or change global.rclone_rc_port.";
        return new RcPortCheck(RcPortCheckOutcome.HeldByOtherProcess, message);
    }

    private static string Who(ProcessDescription holder) =>
        $"PID {holder.ProcessId} ({holder.ImagePath ?? holder.Name ?? "image path unavailable"})";

    private ProcessDescription? Describe(int pid)
    {
        try
        {
            return inspector.Describe(pid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not inspect PID {ProcessId} holding rc port {Port}", pid, options.RcloneRcPort);

            // Unreadable, not absent: report it as an unknown holder so it is never killed.
            return new ProcessDescription { ProcessId = pid };
        }
    }

    /// <summary>Waits (bounded, on the injected clock) for the killed process to exit.</summary>
    private async Task<bool> WaitForExitAsync(int pid, DateTimeOffset start, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + options.StaleKillTimeout;
        while (true)
        {
            if (terminator.HasExited(pid, start))
            {
                return true;
            }

            if (timeProvider.GetUtcNow() + options.StaleKillPollInterval > deadline)
            {
                return false;
            }

            await Task.Delay(options.StaleKillPollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
