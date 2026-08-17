using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.UI.Tray;
using Bosun.Status;

namespace Bosun.Tests.UI.Tray;

public sealed class HostContextMenuBuilderTests
{
    private static HostStatusRow Row(
        MountState state,
        bool isParked = false,
        string? drive = null) => new()
    {
        HostKey = "example",
        DisplayName = "Example",
        State = state,
        Drive = drive,
        UserParked = isParked,
        StatusText = "irrelevant for these tests",
        Mode = MountMode.Persistent,
        SessionCount = 0,
        Category = StatusCategory.Pending,
    };

    private static HostMenuAction Find(IReadOnlyList<HostMenuAction> actions, HostMenuActionKind kind) =>
        Assert.Single(actions, a => a.Kind == kind);

    [Theory]
    [InlineData(MountState.Ready)]
    [InlineData(MountState.Unreachable)]
    public void Mount_IsEnabled_WhenTheHostIsReadyOrUnreachable(MountState state)
    {
        var actions = HostContextMenuBuilder.Build(Row(state));

        Assert.True(Find(actions, HostMenuActionKind.Mount).IsEnabled);
    }

    [Theory]
    [InlineData(MountState.Mounted)]
    [InlineData(MountState.Mounting)]
    [InlineData(MountState.Draining)]
    [InlineData(MountState.Disabled)]
    [InlineData(MountState.Probing)]
    public void Mount_IsDisabled_ForEveryOtherState(MountState state)
    {
        var actions = HostContextMenuBuilder.Build(Row(state));

        Assert.False(Find(actions, HostMenuActionKind.Mount).IsEnabled);
    }

    [Theory]
    [InlineData(MountState.Mounting)]
    [InlineData(MountState.Mounted)]
    public void Unmount_IsEnabled_WhenMountingOrMounted(MountState state)
    {
        var actions = HostContextMenuBuilder.Build(Row(state));

        Assert.True(Find(actions, HostMenuActionKind.Unmount).IsEnabled);
    }

    [Theory]
    [InlineData(MountState.Ready)]
    [InlineData(MountState.Unreachable)]
    [InlineData(MountState.Draining)]
    [InlineData(MountState.Disabled)]
    public void Unmount_IsDisabled_ForEveryOtherState(MountState state)
    {
        var actions = HostContextMenuBuilder.Build(Row(state));

        Assert.False(Find(actions, HostMenuActionKind.Unmount).IsEnabled);
    }

    [Fact]
    public void OpenTerminal_IsAlwaysEnabled_RegardlessOfMountState()
    {
        foreach (var state in Enum.GetValues<MountState>())
        {
            var actions = HostContextMenuBuilder.Build(Row(state));
            Assert.True(Find(actions, HostMenuActionKind.OpenTerminal).IsEnabled);
        }
    }

    [Fact]
    public void OpenInExplorer_IsEnabled_OnlyWhenMountedWithADrive()
    {
        var actions = HostContextMenuBuilder.Build(Row(MountState.Mounted, drive: "P:"));

        Assert.True(Find(actions, HostMenuActionKind.OpenInExplorer).IsEnabled);
    }

    [Fact]
    public void OpenInExplorer_IsDisabled_WhenMountedButNoDriveIsReported()
    {
        var actions = HostContextMenuBuilder.Build(Row(MountState.Mounted, drive: null));

        Assert.False(Find(actions, HostMenuActionKind.OpenInExplorer).IsEnabled);
    }

    [Theory]
    [InlineData(MountState.Ready)]
    [InlineData(MountState.Unreachable)]
    [InlineData(MountState.Draining)]
    public void OpenInExplorer_IsDisabled_WhenNotMounted(MountState state)
    {
        var actions = HostContextMenuBuilder.Build(Row(state, drive: "P:"));

        Assert.False(Find(actions, HostMenuActionKind.OpenInExplorer).IsEnabled);
    }

    [Fact]
    public void Mount_Label_IsPlainMount_WhenNotParked()
    {
        var actions = HostContextMenuBuilder.Build(Row(MountState.Ready, isParked: false));

        Assert.Equal("Mount", Find(actions, HostMenuActionKind.Mount).Label);
    }

    [Fact]
    public void Mount_Label_MentionsUnParking_WhenTheHostUserParked()
    {
        // ADR-015: a parked host must render as deliberate and offer a way to un-park it. The
        // Mount action IS that way -- see HostContextMenuBuilder's remarks -- so its label must
        // say so rather than reading like an ordinary mount.
        var actions = HostContextMenuBuilder.Build(Row(MountState.Ready, isParked: true));

        var label = Find(actions, HostMenuActionKind.Mount).Label;
        Assert.Contains("un-park", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mount_IsStillEnabled_ForAParkedHostThatIsReady()
    {
        // Parking does not change MountState (ADR-015) -- a parked, Ready host must still be
        // mountable via the same action.
        var actions = HostContextMenuBuilder.Build(Row(MountState.Ready, isParked: true));

        Assert.True(Find(actions, HostMenuActionKind.Mount).IsEnabled);
    }

    [Fact]
    public void Build_Throws_WhenRowIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => HostContextMenuBuilder.Build(null!));
    }

    [Fact]
    public void Build_ReturnsExactlyTheFourStandardActions()
    {
        var actions = HostContextMenuBuilder.Build(Row(MountState.Ready));

        Assert.Equal(
            [HostMenuActionKind.Mount, HostMenuActionKind.Unmount, HostMenuActionKind.OpenTerminal, HostMenuActionKind.OpenInExplorer],
            actions.Select(a => a.Kind));
    }
}
