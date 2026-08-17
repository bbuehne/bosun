using Bosun.Configuration;

namespace Bosun.Tests.UI.HostEditor.Fakes;

/// <summary>
/// Test double for the real <c>IHostConfigWriter</c> (owned by a concurrent delivery, bs-ww9.8).
/// Records calls and returns a scripted <see cref="HostConfigWriteResult"/> -- never touches a
/// real file, per CLAUDE.md's worktree-safety rules.
/// </summary>
internal sealed class FakeHostConfigWriter : IHostConfigWriter
{
    public List<HostConfig> SavedHosts { get; } = [];
    public List<string> DeletedHostKeys { get; } = [];

    public HostConfigWriteResult SaveResult { get; set; } = HostConfigWriteResult.Ok();
    public HostConfigWriteResult DeleteResult { get; set; } = HostConfigWriteResult.Ok();

    public Task<HostConfigWriteResult> SaveHostAsync(HostConfig host, CancellationToken cancellationToken = default)
    {
        SavedHosts.Add(host);
        return Task.FromResult(SaveResult);
    }

    public Task<HostConfigWriteResult> DeleteHostAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        DeletedHostKeys.Add(hostKey);
        return Task.FromResult(DeleteResult);
    }
}
