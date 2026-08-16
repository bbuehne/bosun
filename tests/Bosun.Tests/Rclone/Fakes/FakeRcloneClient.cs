using Bosun.Rclone;

namespace Bosun.Tests.Rclone.Fakes;

/// <summary>
/// Scriptable <see cref="IRcloneClient"/> for testing components that depend on it
/// (<see cref="RcloneRemoteProvisioner"/>, <see cref="RemoteRootLister"/>,
/// <see cref="Bosun.Rclone.Process.RcloneProcessService"/>) without a real rc HTTP call
/// (CLAUDE.md worktree-safety rules). Every call is recorded so tests can assert on what was
/// actually sent, not just the observable outcome.
/// </summary>
internal sealed class FakeRcloneClient : IRcloneClient
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _configs = new(StringComparer.Ordinal);
    private readonly Queue<Func<Task<RcloneVersionInfo>>> _versionScript = new();

    public List<string> GetVersionCalls { get; } = [];
    public List<(string RemoteName, string Type, IReadOnlyDictionary<string, string> Parameters)> CreateConfigCalls { get; } = [];
    public List<string> GetConfigCalls { get; } = [];
    public List<RcloneMountRequest> MountCalls { get; } = [];
    public List<string> UnmountCalls { get; } = [];
    public List<(string Fs, string Remote)> ListCalls { get; } = [];

    /// <summary>Seeds a config as if <see cref="CreateConfigAsync"/> had already been called with
    /// it -- lets provisioner tests set up an "already correct" or "stale" starting state.</summary>
    public void SeedConfig(string remoteName, IReadOnlyDictionary<string, string> parameters) =>
        _configs[remoteName] = parameters;

    public void EnqueueVersion(RcloneVersionInfo version) => _versionScript.Enqueue(() => Task.FromResult(version));

    public void EnqueueVersionFailure(Exception exception) => _versionScript.Enqueue(() => Task.FromException<RcloneVersionInfo>(exception));

    public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        GetVersionCalls.Add("core/version");
        return _versionScript.TryDequeue(out var next) ? next() : Task.FromResult(new RcloneVersionInfo { Version = "v1.71.2" });
    }

    public Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        CreateConfigCalls.Add((remoteName, type, parameters));
        _configs[remoteName] = parameters;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken)
    {
        GetConfigCalls.Add(remoteName);
        return Task.FromResult(_configs.TryGetValue(remoteName, out var value) ? value : null);
    }

    public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        MountCalls.Add(request);
        return Task.FromResult(new RcloneMountResult { MountPoint = request.MountPoint });
    }

    public Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        UnmountCalls.Add(mountPoint);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RcloneMountInfo>>([]);

    private Exception? _listThrows;

    public void ListThrows(Exception exception) => _listThrows = exception;

    public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken)
    {
        ListCalls.Add((fs, remote));
        if (_listThrows is not null)
        {
            return Task.FromException<IReadOnlyList<RcloneListItem>>(_listThrows);
        }

        return Task.FromResult<IReadOnlyList<RcloneListItem>>([]);
    }
}
