using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.UI.HostEditor;
using Bosun.Tests.UI.HostEditor.Fakes;

namespace Bosun.Tests.UI.HostEditor;

/// <summary>
/// Deleting a host must unmount it first (Invariant I2).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam where the two halves of bs-ww9.8 disagreed.</b> The config writer and the
/// editor form were built concurrently. <c>HostConfigWriter.DeleteHostAsync</c> deliberately does
/// not drain — it has no access to mount state, which lives in <see cref="IMountSupervisor"/> and
/// not in <c>BosunConfig</c> — and documented that the caller must drain first. The form's
/// <c>DeleteAsync</c> documented the opposite: that draining was the writer's contract.
/// </para>
/// <para>
/// Both positions were individually reasonable, both halves passed their own tests, the merge had
/// no textual conflict, and the result built clean with 814 tests green. Nothing drained. Deleting
/// a mounted host removed it from <c>hosts.toml</c> and left the drive letter live with nothing
/// describing it — the orphaned-mount case I2 exists to prevent. The confirmation dialog even
/// promised the user "its drive will be unmounted first".
/// </para>
/// <para>
/// These tests exist so that gap cannot reopen silently. Fakes only; nothing reaches a real mount,
/// drive letter, rclone, or config file.
/// </para>
/// </remarks>
public sealed class HostEditorDeleteDrainTests
{
    private static HostEditorController Build(
        FakeDeleteSupervisor supervisor,
        FakeHostConfigWriter writer)
    {
        // The store's contents are irrelevant here -- these tests are about the drain that happens
        // before the writer is reached, and the writer is faked.
        var config = new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>(StringComparer.Ordinal),
        };

        return new HostEditorController(writer, new FakeHostConfigStore(config), supervisor);
    }

    [Theory]
    [InlineData(MountState.Mounted)]
    [InlineData(MountState.Mounting)]
    [InlineData(MountState.Draining)]
    public async Task Deleting_a_host_that_holds_a_drive_letter_unmounts_it_before_removing_the_config(MountState state)
    {
        var supervisor = new FakeDeleteSupervisor();
        supervisor.SetHostState("nas", state);
        var writer = new FakeHostConfigWriter();

        var result = await Build(supervisor, writer).DeleteAsync("nas");

        Assert.True(result.Succeeded);
        Assert.Contains("nas", supervisor.UnmountRequests);
        Assert.Contains("nas", writer.DeletedHostKeys);
    }

    [Fact]
    public async Task A_host_that_is_not_mounted_is_deleted_without_a_pointless_unmount()
    {
        var supervisor = new FakeDeleteSupervisor();
        supervisor.SetHostState("nas", MountState.Ready);
        var writer = new FakeHostConfigWriter();

        var result = await Build(supervisor, writer).DeleteAsync("nas");

        Assert.True(result.Succeeded);
        Assert.Empty(supervisor.UnmountRequests);
        Assert.Contains("nas", writer.DeletedHostKeys);
    }

    /// <summary>
    /// If the unmount fails, the delete is abandoned rather than proceeding. A configured host that
    /// will not unmount is recoverable; a live drive letter with no configuration behind it is the
    /// state that wedges Explorer, and writing the config first would produce exactly that.
    /// </summary>
    [Fact]
    public async Task An_unmount_that_fails_abandons_the_delete_rather_than_orphaning_the_drive()
    {
        var supervisor = new FakeDeleteSupervisor { UnmountThrows = new InvalidOperationException("rclone said no") };
        supervisor.SetHostState("nas", MountState.Mounted);
        var writer = new FakeHostConfigWriter();

        var result = await Build(supervisor, writer).DeleteAsync("nas");

        Assert.False(result.Succeeded);
        Assert.Empty(writer.DeletedHostKeys);
        Assert.Contains("nas", result.Error);
    }
}
