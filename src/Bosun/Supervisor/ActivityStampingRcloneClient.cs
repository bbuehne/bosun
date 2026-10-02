using Bosun.Rclone;

namespace Bosun.Supervisor;

/// <summary>
/// Wraps the <see cref="IRcloneClient"/> that <see cref="MountSupervisor"/> uses so every rc call
/// stamps the loop's liveness when it returns (bs-6to). Purely a decorator: it forwards each call
/// unchanged and never alters arguments, results or exceptions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The watchdog reads <see cref="MountSupervisor.LastLoopActivityUtc"/> and calls the
/// loop stalled when it goes stale. Left to the action begin/end stamps alone, a legitimately long
/// composite action -- <c>StartAsync</c> or <c>ResumeAsync</c> over many hosts, each needing several
/// rc calls -- would go quiet for the sum of its parts and look wedged. Stamping per call bounds the
/// quiet gap to ONE call's own timeout (<c>RcloneClient</c>: 60 s for <c>mount/mount</c>, the
/// longest), however many hosts there are.
/// </para>
/// <para>
/// It lives in the supervisor's namespace and is constructed only by <see cref="MountSupervisor"/>,
/// so the rule that only <see cref="IMountSupervisor"/> reaches <c>mount/mount</c> and
/// <c>mount/unmount</c> is unchanged: this type adds no new caller of either.
/// </para>
/// </remarks>
internal sealed class ActivityStampingRcloneClient(IRcloneClient inner, Action stamp) : IRcloneClient
{
    public async Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await inner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }

    public async Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            await inner.CreateConfigAsync(remoteName, type, parameters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.GetConfigAsync(remoteName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }

    public async Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.MountAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }

    public async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        try
        {
            await inner.UnmountAsync(mountPoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }

    public async Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await inner.ListMountsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }

    public async Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.ListAsync(fs, remote, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stamp();
        }
    }
}
