using Bosun.Configuration;
using Bosun.Rclone;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Covers the E3 implementation of E4's <see cref="IRemoteRootLister"/> seam: lists the ROOT of
/// the host's rclone remote (not <c>mount.remote_path</c>), depth 1, via
/// <see cref="IRcloneClient.ListAsync"/>.
/// </summary>
public sealed class RemoteRootListerTests
{
    [Fact]
    public async Task ListRootAsync_lists_the_remote_root_not_the_configured_remote_path()
    {
        var client = new FakeRcloneClient();
        var lister = new RemoteRootLister(client, StoreWith(Host("example-nas", remotePath: "/volume1/share")));

        await lister.ListRootAsync("example-nas", CancellationToken.None);

        var call = Assert.Single(client.ListCalls);
        Assert.Equal("bosun-example-nas:", call.Fs);
        Assert.Equal(string.Empty, call.Remote);
    }

    [Fact]
    public async Task ListRootAsync_works_for_a_shell_only_host_with_no_remote_path_at_all()
    {
        // hosts.example-jump in config/hosts.example.toml: mount.mode = "none", no remote_path.
        // The deep probe still needs to prove SFTP works for such a host.
        var client = new FakeRcloneClient();
        var lister = new RemoteRootLister(client, StoreWith(Host("example-jump", remotePath: null)));

        await lister.ListRootAsync("example-jump", CancellationToken.None);

        var call = Assert.Single(client.ListCalls);
        Assert.Equal("bosun-example-jump:", call.Fs);
    }

    [Fact]
    public async Task ListRootAsync_throws_for_an_unknown_host_key()
    {
        var client = new FakeRcloneClient();
        var lister = new RemoteRootLister(client, StoreWith(Host("example-nas")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => lister.ListRootAsync("no-such-host", CancellationToken.None));
    }

    [Fact]
    public async Task ListRootAsync_propagates_the_rc_failure_so_HostProbe_classifies_it_as_a_normal_deep_probe_failure()
    {
        var client = new FakeRcloneClient();
        client.ListThrows(new RcloneRcException("operations/list", 500, "remote 'bosun-example-nas' not found -- not yet provisioned"));
        var lister = new RemoteRootLister(client, StoreWith(Host("example-nas")));

        await Assert.ThrowsAsync<RcloneRcException>(() => lister.ListRootAsync("example-nas", CancellationToken.None));
    }

    private static FakeHostConfigStore StoreWith(HostConfig host) => new(new BosunConfig
    {
        Global = new GlobalConfig(),
        Hosts = new Dictionary<string, HostConfig> { [host.Key] = host },
    });

    private static HostConfig Host(string key, string? remotePath = "/srv/share") => new()
    {
        Key = key,
        DisplayName = $"Display {key}",
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = new MountConfig { Mode = remotePath is null ? MountMode.None : MountMode.Persistent, Drive = remotePath is null ? null : "N:", RemotePath = remotePath },
        Session = new SessionConfig { Autostart = false, Reconnect = true, Tmux = false, TabColor = "#2D5F3F", ColorScheme = "Campbell" },
        Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = true },
    };
}
