using Bosun.Supervisor;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// The words a failed Mount or Unmount click shows (bs-ngo): the supervisor's own reason for a refusal,
/// a generic sentence for anything unexpected, and never a sentence that blames the user.
/// </summary>
public sealed class HostActionFailureTests
{
    [Fact]
    public void A_refusal_passes_the_supervisors_reason_on_with_the_host_name()
    {
        var failure = HostActionFailure.Describe(
            new MountRequestRefusedException("mercury", "the system is suspended"), "Mercury", isMount: true);

        Assert.Equal("Mercury was not mounted: the system is suspended.", failure.Message);
        Assert.Equal(HostActionFailure.MountTitle, failure.Title);
        Assert.True(failure.IsExpectedRefusal);
    }

    [Fact]
    public void An_unreachable_host_refusal_reads_as_a_condition_of_the_host()
    {
        var failure = HostActionFailure.Describe(
            new MountRequestRefusedException("mercury", "the host did not respond to an immediate reachability check"),
            "Mercury",
            isMount: true);

        Assert.Equal(
            "Mercury was not mounted: the host did not respond to an immediate reachability check.", failure.Message);
    }

    [Theory]
    [InlineData(MountingUnavailableCause.WinFspMissing, "WinFsp is not installed")]
    [InlineData(MountingUnavailableCause.RcloneUnhealthy, "rclone is not running")]
    public void A_mounting_unavailable_refusal_names_the_process_wide_cause(MountingUnavailableCause cause, string reason)
    {
        var failure = HostActionFailure.Describe(new MountingUnavailableException("mercury", cause, reason), "Mercury", isMount: true);

        Assert.Equal($"Mercury was not mounted: {reason}.", failure.Message);
        Assert.True(failure.IsExpectedRefusal);
    }

    [Fact]
    public void A_reason_that_already_ends_in_a_full_stop_does_not_get_a_second()
    {
        var failure = HostActionFailure.Describe(
            new MountingUnavailableException("mercury", MountingUnavailableCause.RcloneUnhealthy, "rclone exited with code 1. "),
            "Mercury",
            isMount: true);

        Assert.Equal("Mercury was not mounted: rclone exited with code 1.", failure.Message);
    }

    [Fact]
    public void A_blank_reason_still_produces_a_sentence()
    {
        var failure = HostActionFailure.Describe(new MountRequestRefusedException("mercury", "  "), "Mercury", isMount: true);

        Assert.Equal("Mercury was not mounted.", failure.Message);
    }

    [Fact]
    public void An_unexpected_exception_gets_a_generic_message_that_leaks_nothing_from_the_exception()
    {
        var failure = HostActionFailure.Describe(
            new InvalidOperationException("secret internal detail at C:\\path\\file.cs:42"), "Mercury", isMount: true);

        Assert.False(failure.IsExpectedRefusal);
        Assert.Equal(
            "Mercury was not mounted, because Bosun hit an unexpected problem. The details are in the Bosun log.", failure.Message);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmount_failure_says_unmounted_and_uses_the_unmount_title()
    {
        var failure = HostActionFailure.Describe(new InvalidOperationException("x"), "Mercury", isMount: false);

        Assert.Equal(HostActionFailure.UnmountTitle, failure.Title);
        Assert.StartsWith("Mercury was not unmounted", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_message_addresses_or_blames_the_user(bool refusal)
    {
        Exception exception = refusal
            ? new MountRequestRefusedException("mercury", "the system is suspended")
            : new InvalidOperationException("boom");

        var message = HostActionFailure.Describe(exception, "Mercury", isMount: true).Message;

        foreach (var word in new[] { "you ", "you'", "your", "should", "please", "mistake", "wrong", "fault" })
        {
            Assert.DoesNotContain(word, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Describe_rejects_a_null_exception_and_an_empty_name()
    {
        Assert.Throws<ArgumentNullException>(() => HostActionFailure.Describe(null!, "Mercury", isMount: true));
        Assert.Throws<ArgumentException>(() => HostActionFailure.Describe(new InvalidOperationException(), "", isMount: true));
    }
}
