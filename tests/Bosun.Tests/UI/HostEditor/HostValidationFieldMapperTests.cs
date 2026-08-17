using Bosun.Configuration;
using Bosun.UI.HostEditor;

namespace Bosun.Tests.UI.HostEditor;

/// <summary>
/// Covers the "the user needs to know *which* field" requirement (bs-ww9.8): mapping a
/// <see cref="ConfigValidationError"/> from a failed save back onto the specific
/// <see cref="HostFormFieldId"/> it concerns.
/// </summary>
public sealed class HostValidationFieldMapperTests
{
    [Theory]
    [InlineData("mount-missing-drive-or-remote-path", "hosts.example: mount.mode requires drive/remote_path")]
    public void Map_MountMissingDriveOrRemotePath_MapsToBothDriveAndRemotePath(string rule, string message)
    {
        var errors = new[] { new ConfigValidationError(rule, message) };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        Assert.Contains(mapped, e => e.Field == HostFormFieldId.Drive);
        Assert.Contains(mapped, e => e.Field == HostFormFieldId.RemotePath);
    }

    [Theory]
    [InlineData("invalid-drive-letter", HostFormFieldId.Drive)]
    [InlineData("invalid-vfs-cache-mode", HostFormFieldId.VfsCacheMode)]
    [InlineData("invalid-network-mode", HostFormFieldId.NetworkMode)]
    [InlineData("negative-idle-unmount-seconds", HostFormFieldId.IdleUnmountSeconds)]
    [InlineData("identity-file-not-found", HostFormFieldId.IdentityFile)]
    [InlineData("negative-probe-interval", HostFormFieldId.ProbeIntervalSeconds)]
    [InlineData("tmux-requires-session", HostFormFieldId.TmuxSession)]
    public void Map_HostSpecificRule_MapsToExpectedField(string rule, HostFormFieldId expected)
    {
        var errors = new[] { new ConfigValidationError(rule, $"hosts.example: {rule} went wrong") };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        var error = Assert.Single(mapped);
        Assert.Equal(expected, error.Field);
    }

    [Fact]
    public void Map_UnrecognisedRule_FallsBackToGeneral()
    {
        var errors = new[] { new ConfigValidationError("some-future-rule", "hosts.example: something new") };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        var error = Assert.Single(mapped);
        Assert.Equal(HostFormFieldId.General, error.Field);
    }

    [Fact]
    public void Map_ErrorForADifferentHost_IsExcluded()
    {
        var errors = new[] { new ConfigValidationError("identity-file-not-found", "hosts.other-host: identity_file '~/x' does not resolve") };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        Assert.Empty(mapped);
    }

    [Fact]
    public void Map_DuplicateDisplayNameError_MapsWhenHostKeyIsAmongTheListedHosts()
    {
        var errors = new[]
        {
            new ConfigValidationError(
                "duplicate-display-name",
                "display_name 'Same Name' is used by more than one host (example, other-host)"),
        };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        var error = Assert.Single(mapped);
        Assert.Equal(HostFormFieldId.DisplayName, error.Field);
    }

    [Fact]
    public void Map_DuplicateDisplayNameError_ExcludedWhenHostKeyIsNotAmongTheListedHosts()
    {
        var errors = new[]
        {
            new ConfigValidationError(
                "duplicate-display-name",
                "display_name 'Same Name' is used by more than one host (host-a, host-b)"),
        };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        Assert.Empty(mapped);
    }

    [Fact]
    public void Map_DriveCollisionError_MapsToDriveField()
    {
        var errors = new[]
        {
            new ConfigValidationError(
                "drive-collision",
                "drive 'N:' is claimed by more than one host (example, other-host)"),
        };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        var error = Assert.Single(mapped);
        Assert.Equal(HostFormFieldId.Drive, error.Field);
    }

    [Fact]
    public void Map_MultipleErrors_OnlyThoseConcerningTheHostAreReturned()
    {
        var errors = new[]
        {
            new ConfigValidationError("identity-file-not-found", "hosts.example: identity_file '~/x' does not resolve"),
            new ConfigValidationError("identity-file-not-found", "hosts.other-host: identity_file '~/y' does not resolve"),
            new ConfigValidationError("invalid-backoff-seconds", "global.backoff_seconds must be non-empty"),
        };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        var error = Assert.Single(mapped);
        Assert.Equal(HostFormFieldId.IdentityFile, error.Field);
    }

    [Fact]
    public void Map_PrefixMatch_DoesNotFalsePositiveOnAKeyThatIsAPrefixOfAnotherHostsKey()
    {
        // "example" must not match a message about "example-2" -- a naive Contains/StartsWith
        // without the trailing colon would.
        var errors = new[]
        {
            new ConfigValidationError("identity-file-not-found", "hosts.example-2: identity_file '~/x' does not resolve"),
        };

        var mapped = HostValidationFieldMapper.Map(errors, "example");

        Assert.Empty(mapped);
    }
}
