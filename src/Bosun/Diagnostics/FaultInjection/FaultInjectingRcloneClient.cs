#if DEBUG
using Bosun.Rclone;
using Bosun.SystemEventIntegration;
using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics.FaultInjection;

/// <summary>
/// Debug-only decorator for <c>BOSUN_FAULT=rcd-unavailable-at-resume</c> (bs-uzk; docs/OPERATIONS.md
/// T9 part E). From each system resume, for a fixed duration, <c>core/version</c> and
/// <c>mount/mount</c> throw as if rcd were not answering; every other call, and every call outside
/// that window, is forwarded unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this shape.</b> The ADR-022 remount record is kept while <c>core/version</c> fails at the
/// resume re-derivation (<c>MountSupervisor.rcdAnswering</c>), then resolved by the 30 s
/// reconciliation tick once <c>core/version</c> answers again, or dropped 10 minutes after the
/// resume. A decorator at the <see cref="IRcloneClient"/> seam drives exactly that real path, with
/// no change to <c>MountSupervisor</c>. It adds no caller of <c>mount/mount</c> or
/// <c>mount/unmount</c>: it only refuses one.
/// </para>
/// <para>
/// <b>When the window opens.</b> On <see cref="ISystemEventSource.Resume"/>. This object is
/// constructed (the first time <see cref="IRcloneClient"/> is resolved) before
/// <c>SystemEventSupervisorAdapter</c> subscribes, and a multicast event runs its handlers in
/// subscription order on one thread, so the window is open before the adapter queues
/// <c>ResumeAsync</c>. A second resume restarts the window.
/// </para>
/// <para>
/// Only <c>core/version</c> and <c>mount/mount</c> fail. <c>mount/unmount</c> and
/// <c>mount/listmounts</c> keep working so the suspend drain and the reconciliation are not
/// disturbed by the fault under test. rcd really is running throughout.
/// </para>
/// </remarks>
internal sealed class FaultInjectingRcloneClient : IRcloneClient, IDisposable
{
    private readonly IRcloneClient _inner;
    private readonly ISystemEventSource _events;
    private readonly TimeProvider _time;
    private readonly TimeSpan _duration;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private DateTimeOffset? _faultUntil;
    private bool _endLogged;

    public FaultInjectingRcloneClient(
        IRcloneClient inner, ISystemEventSource events, TimeProvider time, TimeSpan duration, ILogger logger)
    {
        _inner = inner;
        _events = events;
        _time = time;
        _duration = duration;
        _logger = logger;
        _events.Resume += OnResume;
    }

    public void Dispose() => _events.Resume -= OnResume;

    private void OnResume(object? sender, EventArgs e)
    {
        DateTimeOffset until;
        lock (_gate)
        {
            until = _time.GetUtcNow() + _duration;
            _faultUntil = until;
            _endLogged = false;
        }

        _logger.LogWarning(
            "FAULT INJECTION ACTIVE ({Variable}={Fault}): the system resumed, so core/version and mount/mount fail " +
            "on purpose until {Until:HH:mm:ss} UTC ({Seconds:0.##} s). rclone rcd itself is fine.",
            DebugFaultSettings.VariableName, DebugFaultSettings.RcdUnavailableAtResumeName, until, _duration.TotalSeconds);
    }

    private void ThrowIfInWindow(string endpoint)
    {
        var ended = false;
        lock (_gate)
        {
            if (_faultUntil is not { } until)
            {
                return;
            }

            if (_time.GetUtcNow() < until)
            {
                throw new RcloneRcException(
                    endpoint,
                    null,
                    $"{DebugFaultSettings.VariableName}={DebugFaultSettings.RcdUnavailableAtResumeName}: rcd is reported as " +
                    $"not answering until {until:HH:mm:ss} UTC (injected, not a real failure)");
            }

            if (!_endLogged)
            {
                _endLogged = true;
                ended = true;
            }
        }

        if (ended)
        {
            _logger.LogWarning(
                "FAULT INJECTION ENDED ({Variable}={Fault}): core/version and mount/mount are forwarded to rcd again.",
                DebugFaultSettings.VariableName, DebugFaultSettings.RcdUnavailableAtResumeName);
        }
    }

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        ThrowIfInWindow("core/version");
        return _inner.GetVersionAsync(cancellationToken);
    }

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        ThrowIfInWindow("mount/mount");
        return _inner.MountAsync(request, cancellationToken);
    }

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        _inner.CreateConfigAsync(remoteName, type, parameters, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken) =>
        _inner.GetConfigAsync(remoteName, cancellationToken);

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken) =>
        _inner.UnmountAsync(mountPoint, cancellationToken);

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken) =>
        _inner.ListMountsAsync(cancellationToken);

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken) =>
        _inner.ListAsync(fs, remote, cancellationToken);

    public Task<IReadOnlyList<string>> ListVfsAsync(CancellationToken cancellationToken) =>
        _inner.ListVfsAsync(cancellationToken);

    public Task<RcloneVfsStats> GetVfsStatsAsync(string fs, CancellationToken cancellationToken) =>
        _inner.GetVfsStatsAsync(fs, cancellationToken);

    public Task<IReadOnlyList<RcloneVfsQueueItem>> GetVfsQueueAsync(string fs, CancellationToken cancellationToken) =>
        _inner.GetVfsQueueAsync(fs, cancellationToken);
}
#endif
