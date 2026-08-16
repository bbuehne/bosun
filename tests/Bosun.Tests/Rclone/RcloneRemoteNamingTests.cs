using Bosun.Rclone;

namespace Bosun.Tests.Rclone;

/// <summary>Covers bs-e26's "one remote per host, named by key" mapping.</summary>
public sealed class RcloneRemoteNamingTests
{
    [Fact]
    public void RemoteNameFor_prefixes_the_host_key_so_it_cannot_collide_with_a_bare_user_remote()
    {
        Assert.Equal("bosun-example-nas", RcloneRemoteNaming.RemoteNameFor("example-nas"));
    }

    [Fact]
    public void RemoteFsPath_combines_the_remote_name_and_remote_path_with_a_colon()
    {
        Assert.Equal("bosun-example-nas:/volume1/share", RcloneRemoteNaming.RemoteFsPath("example-nas", "/volume1/share"));
    }

    [Fact]
    public void RemoteFsPath_with_an_empty_remote_path_still_terminates_with_a_colon()
    {
        Assert.Equal("bosun-example-nas:", RcloneRemoteNaming.RemoteFsPath("example-nas", string.Empty));
    }

    [Fact]
    public void Two_different_host_keys_never_produce_the_same_remote_name()
    {
        Assert.NotEqual(RcloneRemoteNaming.RemoteNameFor("prod-web"), RcloneRemoteNaming.RemoteNameFor("prod-web2"));
    }

    [Fact]
    public void RemoteNameFor_rejects_an_empty_host_key()
    {
        Assert.Throws<ArgumentException>(() => RcloneRemoteNaming.RemoteNameFor(string.Empty));
    }
}
