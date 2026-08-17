using Bosun.Configuration;
using Bosun.Supervisor;

namespace Bosun.UI.Tray.Placeholder;

/// <summary>
/// TEMPORARY <see cref="IStatusReadModel"/> -- see <see cref="IStatusReadModel"/>'s remarks for
/// the replacement plan. Exists only so the window and tray built by this epic (bs-ww9.3 /
/// bs-ww9.5) are functional and testable before bs-ww9.6 (the real status read-model, built
/// concurrently in a separate worktree) lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this deliberately does NOT do.</b> bs-ww9.6's brief is explicit that producing GOOD
/// causal status text is "the point of the whole issue" -- distinguishing process-wide
/// unavailability, plain unreachability, reachable-but-repeatedly-failing-to-mount (bs-ww9.4),
/// parked, mounted, and on-demand-resting, each with its own wording, backed by a table-driven test
/// suite. This class does none of that with any rigor: <see cref="BuildStatusText"/> below is a
/// straight line-per-<see cref="MountState"/> switch with no attempt at bs-ww9.6's "say for how
/// long / how many attempts" or the mount-attempt-failure-count case bs-ww9.4 will add fields for.
/// Do not treat this class's wording as a spec to preserve.
/// </para>
/// <para>
/// <b>Aggregate health here is similarly minimal</b> -- see <see cref="GetAggregateHealth"/> --
/// and does not attempt bs-ww9.6's full "healthy / degraded / mounting-unavailable" nuance beyond
/// the three broad rules described on that method.
/// </para>
/// <para>
/// This class is not registered anywhere in <c>BosunHostFactory</c> (deliberately -- see
/// <see cref="IStatusReadModel"/>'s remarks on why UI composition stays out of that file);
/// <c>App.xaml.cs</c> constructs it directly, alongside every other UI-layer type this epic adds.
/// </para>
/// </remarks>
public sealed class PlaceholderStatusReadModel : IStatusReadModel
{
    private readonly IMountSupervisor _supervisor;
    private readonly IHostConfigStore _configStore;

    public PlaceholderStatusReadModel(IMountSupervisor supervisor, IHostConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(configStore);

        _supervisor = supervisor;
        _configStore = configStore;
    }

    public IReadOnlyList<HostStatusRow> GetRows()
    {
        var hosts = _configStore.Current.Hosts;
        var snapshot = _supervisor.GetSnapshot();
        var rows = new List<HostStatusRow>(snapshot.Count);

        foreach (var host in snapshot)
        {
            var displayName = hosts.TryGetValue(host.HostKey, out var config) ? config.DisplayName : host.HostKey;

            rows.Add(new HostStatusRow
            {
                HostKey = host.HostKey,
                DisplayName = displayName,
                State = host.State,
                Drive = host.State == MountState.Mounted ? host.Drive : null,
                IsParked = host.UserParked,
                StatusText = BuildStatusText(host),
            });
        }

        return rows;
    }

    /// <summary>
    /// Deliberately simple, three-rule derivation -- NOT bs-ww9.6's full causal model:
    /// <see cref="AggregateHealth.MountingUnavailable"/> if any host carries a process-wide
    /// <c>MountUnavailableReason</c>; else <see cref="AggregateHealth.Degraded"/> if any
    /// administratively-enabled, non-parked host is <see cref="MountState.Unreachable"/> or
    /// <see cref="MountState.Draining"/>; else <see cref="AggregateHealth.Healthy"/>.
    /// </summary>
    public AggregateHealth GetAggregateHealth()
    {
        var snapshot = _supervisor.GetSnapshot();

        if (snapshot.Any(h => h.MountUnavailableReason is not null))
        {
            return AggregateHealth.MountingUnavailable;
        }

        var isDegraded = snapshot.Any(h =>
            h.AdministrativelyEnabled
            && !h.UserParked
            && h.State is MountState.Unreachable or MountState.Draining);

        return isDegraded ? AggregateHealth.Degraded : AggregateHealth.Healthy;
    }

    private static string BuildStatusText(HostMountSnapshot host)
    {
        if (host.MountUnavailableReason is { } reason)
        {
            return $"Not mounted — {reason}";
        }

        if (host.UserParked)
        {
            return "Parked (unmounted by request)";
        }

        return host.State switch
        {
            MountState.Mounted when host.Drive is not null => $"Mounted at {host.Drive}",
            MountState.Mounted => "Mounted",
            MountState.Mounting => "Mounting…",
            MountState.Draining => "Unmounting…",
            MountState.Unreachable => "Unreachable",
            MountState.Probing => "Probing…",
            MountState.Ready => "Reachable, not mounted",
            MountState.Disabled => "Disabled",
            _ => host.State.ToString(),
        };
    }
}
