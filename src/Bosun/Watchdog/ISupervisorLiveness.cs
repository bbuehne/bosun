namespace Bosun.Watchdog;

/// <summary>
/// The narrow, read-only view of the mount supervisor that the watchdog depends on (bs-6to,
/// ADR-020 Decision 3). Three facts, no behaviour: the watchdog must not be able to call the
/// supervisor, because it has to keep working when the supervisor does not.
/// </summary>
/// <remarks>
/// Implemented by <see cref="Supervisor.MountSupervisor"/>. All three members are safe to read from
/// any thread and never block.
/// </remarks>
public interface ISupervisorLiveness
{
    /// <summary>The supervisor has been started and not stopped, i.e. it is supposed to be doing
    /// work. A supervisor that is not started legitimately has no activity.</summary>
    bool IsStarted { get; }

    /// <summary>The loop that processes every supervisor command is alive. False before it has
    /// started and after it has exited for any reason.</summary>
    bool IsLoopRunning { get; }

    /// <summary>When, by the injected clock, the loop last started, began an action, finished an
    /// action, or got an rc reply. <see cref="DateTimeOffset.MinValue"/> before the loop started.</summary>
    DateTimeOffset LastLoopActivityUtc { get; }
}

/// <summary>
/// Reads liveness from the <see cref="Supervisor.MountSupervisor"/> in the container, resolving it
/// on first use and treating "cannot be resolved" as "not started".
/// </summary>
/// <remarks>
/// <c>StartupOrchestrator</c> resolves the supervisor inside its own try/catch, so a supervisor that
/// cannot be built degrades Bosun (reported as <c>startup.supervisor-failed</c>) instead of aborting
/// the host (ADR-012). The watchdog starts alongside it and must not undo that: if it resolved the
/// supervisor eagerly, the same construction failure would escape from <c>IHostedService.StartAsync</c>
/// and take the whole application down. With no supervisor there is nothing to be stalled.
/// </remarks>
public sealed class ServiceProviderSupervisorLiveness(IServiceProvider services) : ISupervisorLiveness
{
    private ISupervisorLiveness? resolved;

    public bool IsStarted => Resolve()?.IsStarted ?? false;

    public bool IsLoopRunning => Resolve()?.IsLoopRunning ?? false;

    public DateTimeOffset LastLoopActivityUtc => Resolve()?.LastLoopActivityUtc ?? DateTimeOffset.MinValue;

    private ISupervisorLiveness? Resolve()
    {
        if (resolved is not null)
        {
            return resolved;
        }

        try
        {
            return resolved = (ISupervisorLiveness?)services.GetService(typeof(Supervisor.MountSupervisor));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
