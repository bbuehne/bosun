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

    // Cases below are derived from rclone's configNameRe in fs/fspath/path.go
    // (https://github.com/rclone/rclone/blob/master/fs/fspath/path.go):
    //   [\w\p{L}\p{N}.+@]+(?:[ -]+[\w\p{L}\p{N}.+@-]+)*
    // i.e. letters/digits/_ . + @ plus interior spaces and hyphens; not starting with '-' or
    // space and not ending with space.

    [Theory]
    [InlineData("bosun-example-nas")]
    [InlineData("bosun-a_b.c+d@e")]
    [InlineData("bosun-two words")]
    [InlineData("bosun-café")]
    [InlineData("bosun-١٢")] // Arabic-Indic digits: \p{N}
    [InlineData("bosun-x-")] // a trailing hyphen is allowed; only a trailing space is not
    [InlineData("bosun-x - y")] // mixed space/hyphen run in the middle
    [InlineData("a")]
    public void IsLegalRemoteName_accepts_names_rclone_accepts(string name)
    {
        Assert.True(RcloneRemoteNaming.IsLegalRemoteName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading-hyphen")]
    [InlineData(" leading-space")]
    [InlineData("trailing-space ")]
    [InlineData("has:colon")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has,comma")]
    [InlineData("has!bang")]
    [InlineData("tab\there")]
    [InlineData("newline\n")] // .NET '$' would let a trailing newline through; \z must not
    [InlineData("trailing-newline-after-valid\n")]
    public void IsLegalRemoteName_rejects_names_rclone_rejects(string name)
    {
        Assert.False(RcloneRemoteNaming.IsLegalRemoteName(name));
    }

    [Theory]
    [InlineData("example-nas")]
    [InlineData("nas_2")]
    [InlineData("Prod01")]
    public void Every_TOML_bare_key_gives_a_legal_remote_name_because_of_the_prefix(string bareKey)
    {
        Assert.True(RcloneRemoteNaming.IsLegalRemoteName(RcloneRemoteNaming.RemoteNameFor(bareKey)));
    }

    [Fact]
    public void RemoteNameFor_rejects_an_empty_host_key()
    {
        Assert.Throws<ArgumentException>(() => RcloneRemoteNaming.RemoteNameFor(string.Empty));
    }
}
