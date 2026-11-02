using Bosun.Configuration;

namespace Bosun.Tests.Configuration;

/// <summary>
/// bs-3ip: environment-variable expansion of <c>global.rclone_config_path</c> happens in exactly
/// one place, <see cref="GlobalConfig.ResolveRcloneConfigPath"/>, and the stored value stays
/// literal so a config rewrite round-trips what the user wrote.
/// </summary>
public sealed class GlobalConfigRcloneConfigPathTests
{
    [Fact]
    public void ResolveRcloneConfigPath_passes_the_literal_stored_value_through_the_expander()
    {
        var global = new GlobalConfig { RcloneConfigPath = @"%APPDATA%\rclone\rclone.conf" };
        string? seen = null;

        var resolved = global.ResolveRcloneConfigPath(raw =>
        {
            seen = raw;
            return raw.Replace("%APPDATA%", @"C:\Fake\Roaming", StringComparison.Ordinal);
        });

        Assert.Equal(@"%APPDATA%\rclone\rclone.conf", seen);
        Assert.Equal(@"C:\Fake\Roaming\rclone\rclone.conf", resolved);
    }

    [Fact]
    public void RcloneConfigPath_itself_stays_unexpanded_after_resolving()
    {
        var global = new GlobalConfig();

        _ = global.ResolveRcloneConfigPath(_ => "expanded");

        Assert.Equal(GlobalConfig.DefaultRcloneConfigPath, global.RcloneConfigPath);
    }

    [Fact]
    public void ResolveRcloneConfigPath_default_expander_leaves_a_path_with_no_tokens_unchanged()
    {
        var global = new GlobalConfig { RcloneConfigPath = @"C:\fixture\rclone.conf" };

        Assert.Equal(@"C:\fixture\rclone.conf", global.ResolveRcloneConfigPath());
    }

    [Fact]
    public void ResolveRcloneConfigPath_default_expander_is_Environment_ExpandEnvironmentVariables()
    {
        // Behaviour-preservation pin: what the DI wiring point used to do inline.
        var global = new GlobalConfig();

        Assert.Equal(
            Environment.ExpandEnvironmentVariables(GlobalConfig.DefaultRcloneConfigPath),
            global.ResolveRcloneConfigPath());
    }

    [Fact]
    public void ConfigParser_does_not_expand_the_path_at_bind_time()
    {
        var config = ConfigParser.Parse("""
            [global]
            rclone_config_path = "%APPDATA%\\custom\\rclone.conf"
            """);

        Assert.Equal(@"%APPDATA%\custom\rclone.conf", config.Global.RcloneConfigPath);
    }
}
