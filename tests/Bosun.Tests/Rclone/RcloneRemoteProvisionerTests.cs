using Bosun.Configuration;
using Bosun.Rclone;
using Bosun.Tests.Rclone.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Covers bs-e26's <see cref="RcloneRemoteProvisioner"/>: mapping a <see cref="HostConfig"/> to
/// rclone <c>sftp</c> parameters, idempotency, and never touching an unrelated remote.
/// </summary>
public sealed class RcloneRemoteProvisionerTests
{
    [Fact]
    public async Task EnsureRemoteAsync_creates_the_remote_with_host_port_user_and_key_file_mapped()
    {
        var client = new FakeRcloneClient();
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);

        await provisioner.EnsureRemoteAsync(Host("example-nas", "nas.example.internal", 22, "barry", "/home/barry/.ssh/id_ed25519"), CancellationToken.None);

        var call = Assert.Single(client.CreateConfigCalls);
        Assert.Equal("bosun-example-nas", call.RemoteName);
        Assert.Equal("sftp", call.Type);
        Assert.Equal("nas.example.internal", call.Parameters["host"]);
        Assert.Equal("22", call.Parameters["port"]);
        Assert.Equal("barry", call.Parameters["user"]);
        Assert.Equal("/home/barry/.ssh/id_ed25519", call.Parameters["key_file"]);
    }

    [Fact]
    public async Task EnsureRemoteAsync_forces_ask_password_false_so_it_never_prompts_interactively()
    {
        // Invariant I10 / ADR-007: key-based auth only.
        var client = new FakeRcloneClient();
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);

        await provisioner.EnsureRemoteAsync(Host("example-nas"), CancellationToken.None);

        Assert.Equal("false", client.CreateConfigCalls[0].Parameters["ask_password"]);
        Assert.False(client.CreateConfigCalls[0].Parameters.ContainsKey("pass"));
    }

    [Fact]
    public async Task EnsureRemoteAsync_expands_a_leading_tilde_in_identity_file_the_same_way_ConfigValidator_does()
    {
        var client = new FakeRcloneClient();
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);
        var host = Host("example-nas", identityFile: "~/.ssh/id_ed25519");

        await provisioner.EnsureRemoteAsync(host, CancellationToken.None);

        var expected = ConfigValidator.ExpandHome("~/.ssh/id_ed25519");
        Assert.Equal(expected, client.CreateConfigCalls[0].Parameters["key_file"]);
        Assert.DoesNotContain("~", client.CreateConfigCalls[0].Parameters["key_file"]);
    }

    [Fact]
    public async Task EnsureRemoteAsync_is_a_no_op_when_the_remote_already_matches()
    {
        var client = new FakeRcloneClient();
        var host = Host("example-nas", "nas.example.internal", 22, "barry", "/home/barry/.ssh/id_ed25519");
        client.SeedConfig("bosun-example-nas", new Dictionary<string, string>
        {
            ["host"] = "nas.example.internal",
            ["port"] = "22",
            ["user"] = "barry",
            ["key_file"] = "/home/barry/.ssh/id_ed25519",
            ["ask_password"] = "false",
            ["type"] = "sftp", // extra key rclone would echo back -- must not force a rewrite
        });
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);

        await provisioner.EnsureRemoteAsync(host, CancellationToken.None);

        Assert.Empty(client.CreateConfigCalls);
    }

    [Fact]
    public async Task EnsureRemoteAsync_recreates_the_remote_when_a_mapped_field_has_drifted()
    {
        var client = new FakeRcloneClient();
        var host = Host("example-nas", "nas.example.internal", 2222, "barry", "/home/barry/.ssh/id_ed25519");
        client.SeedConfig("bosun-example-nas", new Dictionary<string, string>
        {
            ["host"] = "nas.example.internal",
            ["port"] = "22", // stale -- host now uses 2222
            ["user"] = "barry",
            ["key_file"] = "/home/barry/.ssh/id_ed25519",
            ["ask_password"] = "false",
        });
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);

        await provisioner.EnsureRemoteAsync(host, CancellationToken.None);

        var call = Assert.Single(client.CreateConfigCalls);
        Assert.Equal("2222", call.Parameters["port"]);
    }

    [Fact]
    public async Task EnsureRemoteAsync_only_ever_names_the_one_remote_it_owns_for_this_host()
    {
        // bs-e26 "don't clobber unrelated user remotes": the provisioner never even learns
        // about, let alone writes, any name other than this host's own bosun-<key> remote.
        var client = new FakeRcloneClient();
        client.SeedConfig("some-other-users-remote", new Dictionary<string, string> { ["host"] = "unrelated" });
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);

        await provisioner.EnsureRemoteAsync(Host("example-nas"), CancellationToken.None);

        Assert.All(client.CreateConfigCalls, call => Assert.Equal("bosun-example-nas", call.RemoteName));
        Assert.All(client.GetConfigCalls, name => Assert.Equal("bosun-example-nas", name));
    }

    [Fact]
    public async Task EnsureAllRemotesAsync_provisions_every_host_in_the_config()
    {
        var client = new FakeRcloneClient();
        var provisioner = new RcloneRemoteProvisioner(client, NullLogger<RcloneRemoteProvisioner>.Instance);
        var config = new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>
            {
                ["example-nas"] = Host("example-nas"),
                ["example-jump"] = Host("example-jump"), // mount.mode = none -- still provisioned; deep_probe needs it too
            },
        };

        await provisioner.EnsureAllRemotesAsync(config, CancellationToken.None);

        Assert.Equal(2, client.CreateConfigCalls.Count);
        Assert.Contains(client.CreateConfigCalls, c => c.RemoteName == "bosun-example-nas");
        Assert.Contains(client.CreateConfigCalls, c => c.RemoteName == "bosun-example-jump");
    }

    private static HostConfig Host(
        string key,
        string hostname = "example.internal",
        int port = 22,
        string user = "someuser",
        string identityFile = "/home/someuser/.ssh/id_ed25519") => new()
    {
        Key = key,
        DisplayName = $"Display {key}",
        Hostname = hostname,
        Port = port,
        User = user,
        IdentityFile = identityFile,
        Mount = new MountConfig { Mode = MountMode.None },
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = true,
            Tmux = false,
            TabColor = "#2D5F3F",
            ColorScheme = "Campbell",
        },
        Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = true },
    };
}
