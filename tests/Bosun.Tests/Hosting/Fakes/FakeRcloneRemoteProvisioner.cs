using Bosun.Configuration;
using Bosun.Rclone;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// Scriptable <see cref="IRcloneRemoteProvisioner"/> for <c>StartupOrchestrator</c> tests: lets a
/// test make provisioning fail for one specific host while every other host still succeeds, which
/// the real <see cref="RcloneRemoteProvisioner"/> backed by a bare <see cref="FakeRcloneClient"/>
/// has no simple way to express (bs-6f9's acceptance criterion: "provisioning fails for one host
/// but not others").
/// </summary>
internal sealed class FakeRcloneRemoteProvisioner(List<string>? order = null) : IRcloneRemoteProvisioner
{
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    public List<string> EnsureRemoteCalls { get; } = [];

    public void FailFor(string hostKey, Exception exception) => _failures[hostKey] = exception;

    public Task EnsureRemoteAsync(HostConfig host, CancellationToken cancellationToken)
    {
        EnsureRemoteCalls.Add(host.Key);
        order?.Add($"provision:{host.Key}");

        return _failures.TryGetValue(host.Key, out var exception)
            ? Task.FromException(exception)
            : Task.CompletedTask;
    }

    public async Task EnsureAllRemotesAsync(BosunConfig config, CancellationToken cancellationToken)
    {
        foreach (var host in config.Hosts.Values)
        {
            await EnsureRemoteAsync(host, cancellationToken).ConfigureAwait(false);
        }
    }
}
