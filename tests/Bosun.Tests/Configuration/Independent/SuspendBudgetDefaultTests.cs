using Bosun.Configuration;

namespace Bosun.Tests.Configuration.Independent;

/// <summary>
/// bs-u4a item 1 / bs-ajh, from docs/CONFIG-SCHEMA.md's <c>global.suspend_unmount_timeout_seconds</c>
/// row: "Default <c>3</c>". The end-to-end half (the supervisor actually escalating 3 s after suspend
/// when nothing sets the value) is
/// <c>SuspendWhileDrainingSpecTests.With_the_default_config_a_mounted_host_escalates_three_seconds_after_suspend</c>.
/// </summary>
/// <remarks>Parses only in-line TOML text; never reads the real, gitignored config/hosts.toml.</remarks>
public sealed class SuspendBudgetDefaultTests
{
    /// <summary>
    /// <b>Catches:</b> a <see cref="GlobalConfig"/> built in code (tests, the inert stand-in, any
    /// future "no config yet" path) still defaulting to the old 8 s, which is four times longer than
    /// Windows waits for PBT_APMSUSPEND.
    /// </summary>
    [Fact]
    public void A_GlobalConfig_built_in_code_defaults_the_suspend_budget_to_3_seconds()
    {
        Assert.Equal(3, new GlobalConfig().SuspendUnmountTimeoutSeconds);
    }

    /// <summary>
    /// <b>Catches:</b> the parser's fallback for an absent key drifting from the documented default,
    /// both when <c>[global]</c> exists without the key and when there is no <c>[global]</c> at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("[global]\nprobe_timeout_seconds = 5\n")]
    public void An_absent_suspend_unmount_timeout_parses_as_3_seconds(string toml)
    {
        Assert.Equal(3, ConfigParser.Parse(toml).Global.SuspendUnmountTimeoutSeconds);
    }

    /// <summary>
    /// <b>Catches:</b> a parser that ignores the key and always yields the default, which would make
    /// the two tests above pass for the wrong reason.
    /// </summary>
    [Fact]
    public void An_explicit_suspend_unmount_timeout_is_honoured()
    {
        Assert.Equal(7, ConfigParser.Parse("[global]\nsuspend_unmount_timeout_seconds = 7\n").Global.SuspendUnmountTimeoutSeconds);
    }

    /// <summary>
    /// <b>Catches:</b> a validator rule (a minimum above 3, say) that rejects the documented default,
    /// so a config that never mentions the key would be rejected wholesale. Also checks the
    /// documented lower bound still holds: zero is rejected.
    /// </summary>
    [Fact]
    public void The_validator_accepts_the_default_budget_and_still_rejects_zero()
    {
        static bool HasSuspendError(BosunConfig config) =>
            ConfigValidator.Validate(config, _ => true).Errors
                .Any(e => e.Rule.Contains("suspend", StringComparison.OrdinalIgnoreCase));

        var byDefault = ConfigParser.Parse("[global]\n");
        var zero = ConfigParser.Parse("[global]\nsuspend_unmount_timeout_seconds = 0\n");

        Assert.False(HasSuspendError(byDefault));
        Assert.True(HasSuspendError(zero));
    }
}
