using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.UI.Tray.Fakes;
using Bosun.UI.Tray;
using Bosun.Status;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// Invariant I4 / ARCHITECTURE.md §3: only <c>IMountSupervisor</c> may cause a mount/unmount.
/// These tests pin that <see cref="HostActionDispatcher"/> only ever reaches
/// <c>IMountSupervisor</c> and <c>IExternalLauncher</c> -- never anything else -- and routes each
/// action kind correctly.
/// </summary>
public sealed class HostActionDispatcherTests
{
    private static HostStatusRow Row(string hostKey = "example", string? drive = null) => new()
    {
        HostKey = hostKey,
        DisplayName = hostKey,
        State = MountState.Ready,
        Drive = drive,
        UserParked = false,
        StatusText = "irrelevant",
        Mode = MountMode.Persistent,
        SessionCount = 0,
        Category = StatusCategory.Pending,
    };

    [Fact]
    public void Dispatch_Mount_CallsRequestMountAsync_WithTheHostKey()
    {
        var supervisor = new FakeMountSupervisor();
        var dispatcher = new HostActionDispatcher(supervisor, new FakeExternalLauncher());

        dispatcher.Dispatch(HostMenuActionKind.Mount, Row("example-nas"));

        Assert.Equal(["example-nas"], supervisor.MountRequests);
        Assert.Empty(supervisor.UnmountRequests);
    }

    [Fact]
    public void Dispatch_Unmount_CallsRequestUnmountAsync_WithTheHostKey()
    {
        var supervisor = new FakeMountSupervisor();
        var dispatcher = new HostActionDispatcher(supervisor, new FakeExternalLauncher());

        dispatcher.Dispatch(HostMenuActionKind.Unmount, Row("example-nas"));

        Assert.Equal(["example-nas"], supervisor.UnmountRequests);
        Assert.Empty(supervisor.MountRequests);
    }

    [Fact]
    public void Dispatch_OpenTerminal_CallsTheLauncher_NeverTheSupervisor()
    {
        var supervisor = new FakeMountSupervisor();
        var launcher = new FakeExternalLauncher();
        var dispatcher = new HostActionDispatcher(supervisor, launcher);

        dispatcher.Dispatch(HostMenuActionKind.OpenTerminal, Row("example-nas"));

        Assert.Equal(["example-nas"], launcher.TerminalOpens);
        Assert.Empty(supervisor.MountRequests);
        Assert.Empty(supervisor.UnmountRequests);
    }

    [Fact]
    public void Dispatch_OpenInExplorer_CallsTheLauncher_WithTheDrive()
    {
        var launcher = new FakeExternalLauncher();
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), launcher);

        dispatcher.Dispatch(HostMenuActionKind.OpenInExplorer, Row("example-nas", drive: "P:"));

        Assert.Equal(["P:"], launcher.ExplorerOpens);
    }

    [Fact]
    public void Dispatch_OpenInExplorer_IsANoOp_WhenTheRowHasNoDrive()
    {
        var launcher = new FakeExternalLauncher();
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), launcher);

        dispatcher.Dispatch(HostMenuActionKind.OpenInExplorer, Row("example-nas", drive: null));

        Assert.Empty(launcher.ExplorerOpens);
    }

    [Fact]
    public void Dispatch_Mount_DoesNotThrow_WhenTheSupervisorRefusesTheRequest()
    {
        // MountRequestRefusedException (e.g. suspended) is a normal, expected outcome from the UI's
        // point of view -- logged, never surfaced as a crash.
        var supervisor = new FakeMountSupervisor { MountRequestException = new InvalidOperationException("refused") };
        var dispatcher = new HostActionDispatcher(supervisor, new FakeExternalLauncher());

        var exception = Record.Exception(() => dispatcher.Dispatch(HostMenuActionKind.Mount, Row()));

        Assert.Null(exception);
    }

    [Fact]
    public void Dispatch_Throws_ForAnUnhandledActionKind()
    {
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), new FakeExternalLauncher());

        Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.Dispatch((HostMenuActionKind)999, Row()));
    }

    [Fact]
    public void Dispatch_Throws_WhenRowIsNull()
    {
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), new FakeExternalLauncher());

        Assert.Throws<ArgumentNullException>(() => dispatcher.Dispatch(HostMenuActionKind.Mount, null!));
    }

    [Fact]
    public void Constructor_Throws_ForNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new HostActionDispatcher(null!, new FakeExternalLauncher()));
        Assert.Throws<ArgumentNullException>(() => new HostActionDispatcher(new FakeMountSupervisor(), null!));
    }
}
