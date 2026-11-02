using Bosun.Health;

namespace Bosun.UI.Tray;

/// <summary>
/// Raises one toast when Bosun itself enters a Faulted state (bs-ww9.1.1; ADR-012's channel table
/// prescribes "persistent error icon plus one toast" for a missing dependency and "toast
/// immediately" for an invalid config, and until now only the unexpected-unmount balloon existed).
/// Subscribed to <see cref="IAppHealth.Changed"/>; has no UI-framework dependency, so it is
/// unit-tested with a fake <see cref="IAppHealth"/> (and, for the grace period, the real
/// <c>AppHealthService</c> on a fake clock).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rule.</b> A toast is raised for each Faulted issue <i>code</i> that is newly present, with
/// the title <c>Bosun</c> and the issue's <see cref="HealthIssue.Title"/> as text. Degraded issues
/// never toast. A code that stays Faulted does not toast again, however often the issue's title or
/// detail changes; it can toast again only after it has cleared (or dropped to Degraded) and come
/// back, so a fault that cycles on a retry loop produces one toast per episode of "it is broken",
/// not one per retry.
/// </para>
/// <para>
/// <b>Startup grace period.</b> While <see cref="AppHealth.IsStarting"/> is true (rclone has not
/// come up yet and the grace period has not run out) nothing is raised. The model owns that
/// definition, so this class neither reads a clock nor re-implements it. Faults seen during the
/// grace period are not forgotten: the first evaluation after it ends raises one toast for each
/// Faulted code still present. Otherwise a missing WinFsp, reported while rclone is still starting,
/// would get its toast never, which is the case ADR-012 most wants a toast for.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="IAppHealth.Changed"/> fires on whichever thread caused the change.
/// State is guarded by a lock, and the callback runs on that thread, outside the lock; the caller
/// marshals to its UI thread. Wording is the issue's own title, which names the condition and
/// never the user.
/// </para>
/// </remarks>
public sealed class DegradedStateNotifier : IDisposable
{
    /// <summary>The toast title.</summary>
    public const string ToastTitle = "Bosun";

    private readonly IAppHealth _health;
    private readonly Action<string, string> _show;
    private readonly object _gate = new();
    private readonly HashSet<string> _faultedCodes = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="health">The health model to watch. Its current value is evaluated once on
    /// construction, so a fault that is already present is not missed.</param>
    /// <param name="show">Raises a toast: <c>(title, text)</c>. May be called from any thread.</param>
    public DegradedStateNotifier(IAppHealth health, Action<string, string> show)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(show);

        _health = health;
        _show = show;

        _health.Changed += OnChanged;
        Evaluate(_health.Current);
    }

    private void OnChanged(object? sender, AppHealthChangedEventArgs e) => Evaluate(e.Health);

    private void Evaluate(AppHealth health)
    {
        List<HealthIssue>? toRaise = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var faulted = health.Issues.Where(i => i.Severity == HealthLevel.Faulted).ToList();

            // A code that is no longer Faulted is forgotten, so it can toast again if it returns.
            _faultedCodes.IntersectWith(faulted.Select(i => i.Code));

            if (health.IsStarting)
            {
                return;
            }

            foreach (var issue in faulted)
            {
                if (_faultedCodes.Add(issue.Code))
                {
                    (toRaise ??= []).Add(issue);
                }
            }
        }

        if (toRaise is null)
        {
            return;
        }

        foreach (var issue in toRaise)
        {
            _show(ToastTitle, issue.Title);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _health.Changed -= OnChanged;
    }
}
