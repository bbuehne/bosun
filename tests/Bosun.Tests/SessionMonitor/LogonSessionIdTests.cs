using Bosun.SessionMonitor;

namespace Bosun.Tests.SessionMonitor;

public sealed class LogonSessionIdTests
{
    private static readonly DateTime Logon = new(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Equal_luid_and_logon_time_are_equal()
    {
        Assert.Equal(new LogonSessionId(0x1234, Logon), new LogonSessionId(0x1234, Logon));
        Assert.True(new LogonSessionId(0x1234, Logon) == new LogonSessionId(0x1234, Logon));
        Assert.Equal(
            new LogonSessionId(0x1234, Logon).GetHashCode(),
            new LogonSessionId(0x1234, Logon).GetHashCode());
    }

    [Fact]
    public void A_different_luid_is_a_different_session()
    {
        Assert.NotEqual(new LogonSessionId(0x1234, Logon), new LogonSessionId(0x1235, Logon));
    }

    [Fact]
    public void The_same_luid_with_a_different_logon_time_is_a_different_session()
    {
        // LUIDs are per boot: after a reboot the same value can name a different logon.
        Assert.NotEqual(new LogonSessionId(0x1234, Logon), new LogonSessionId(0x1234, Logon.AddHours(10)));
    }

    [Fact]
    public void An_unknown_logon_time_is_not_equal_to_a_known_one()
    {
        Assert.NotEqual(new LogonSessionId(0x1234, Logon), new LogonSessionId(0x1234, null));
        Assert.Equal(new LogonSessionId(0x1234, null), new LogonSessionId(0x1234, null));
    }
}
