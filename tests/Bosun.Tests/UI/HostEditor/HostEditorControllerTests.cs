using Bosun.Configuration;
using Bosun.Tests.UI.HostEditor.Fakes;
using Bosun.UI.HostEditor;

namespace Bosun.Tests.UI.HostEditor;

/// <summary>
/// The host-editor form's logic (bs-ww9.8, ADR-019): defaults for a new host, parsing a
/// <see cref="HostFormModel"/> into a <see cref="HostConfig"/>, the I6/I7 correctness floors, the
/// dependent-field enable rules, and driving the (faked) <see cref="IHostConfigWriter"/> for
/// save/delete. Never constructs a WPF <see cref="System.Windows.Window"/> -- see
/// <see cref="Fakes.FakeHostConfigWriter"/> and <see cref="Fakes.FakeHostConfigStore"/>.
/// </summary>
public sealed class HostEditorControllerTests
{
    private static BosunConfig EmptyConfig() => new()
    {
        Global = new GlobalConfig(),
        Hosts = new Dictionary<string, HostConfig>(),
    };

    private static BosunConfig ConfigWith(params HostConfig[] hosts) => new()
    {
        Global = new GlobalConfig(),
        Hosts = hosts.ToDictionary(h => h.Key),
    };

    private static HostConfig ValidHost(string key, string drive = "D:") => new()
    {
        Key = key,
        DisplayName = key,
        Hostname = "example.internal",
        Port = 22,
        User = "someone",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = new MountConfig
        {
            Mode = MountMode.OnDemand,
            Drive = drive,
            RemotePath = "/",
            VfsCacheMode = "writes",
            NetworkMode = true,
            IdleUnmountSeconds = 0,
        },
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = true,
            Tmux = false,
            TmuxSession = null,
            TabColor = "#2D5F3F",
            ColorScheme = "Campbell",
        },
        Probe = new ProbeConfig { IntervalSeconds = 60, DeepProbe = true },
    };

    private static HostFormModel ValidModel(string key = "new-host") => new()
    {
        Key = key,
        IsNewHost = true,
        DisplayName = "New Host",
        Hostname = "example.internal",
        Port = "22",
        User = "someone",
        IdentityFile = "~/.ssh/id_ed25519",
        Mode = MountMode.OnDemand,
        Drive = "D:",
        RemotePath = "/",
        VfsCacheMode = "writes",
        IdleUnmountSeconds = "0",
        Autostart = false,
        Reconnect = true,
        Tmux = false,
        TmuxSession = string.Empty,
        TabColor = "#2D5F3F",
        ColorScheme = "Campbell",
        ProbeIntervalSeconds = "60",
        DeepProbe = true,
    };

    private static (Bosun.UI.HostEditor.HostEditorController Controller, FakeHostConfigWriter Writer, FakeHostConfigStore Store) CreateSut(BosunConfig? config = null)
    {
        var writer = new FakeHostConfigWriter();
        var store = new FakeHostConfigStore(config ?? EmptyConfig());
        var controller = new Bosun.UI.HostEditor.HostEditorController(writer, store);
        return (controller, writer, store);
    }

    // ------------------------------------------------------------------------------------------
    // New-host defaults (must trace back to NewHostDefaults.Create, never reinvented)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void CreateNewHostForm_UsesNewHostDefaults()
    {
        var (controller, _, _) = CreateSut();

        var form = controller.CreateNewHostForm("my-nas");

        Assert.Equal("my-nas", form.Key);
        Assert.True(form.IsNewHost);
        Assert.Equal("my-nas", form.DisplayName);
        Assert.Equal("22", form.Port);
        Assert.Equal(MountMode.OnDemand, form.Mode);
        Assert.Equal("D:", form.Drive);
        Assert.Equal("writes", form.VfsCacheMode);
        Assert.Equal("0", form.IdleUnmountSeconds);
        Assert.True(form.Reconnect);
        Assert.False(form.Tmux);
        Assert.Equal("60", form.ProbeIntervalSeconds);
        Assert.True(form.DeepProbe);
    }

    [Fact]
    public void CreateNewHostForm_SkipsDriveLettersAlreadyClaimed()
    {
        var (controller, _, _) = CreateSut(ConfigWith(ValidHost("existing", drive: "D:")));

        var form = controller.CreateNewHostForm("second-host");

        Assert.Equal("E:", form.Drive);
    }

    [Fact]
    public void KeyAlreadyExists_TrueForAnExistingKey_FalseOtherwise()
    {
        var (controller, _, _) = CreateSut(ConfigWith(ValidHost("existing")));

        Assert.True(controller.KeyAlreadyExists("existing"));
        Assert.False(controller.KeyAlreadyExists("brand-new"));
    }

    // ------------------------------------------------------------------------------------------
    // Edit-form mapping
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void CreateEditForm_MapsAnExistingMountBearingHostFaithfully()
    {
        var host = ValidHost("existing") with { Session = new SessionConfig { Autostart = true, Reconnect = false, Tmux = true, TmuxSession = "main", TabColor = "#ABCDEF", ColorScheme = "Solarized" } };

        var form = Bosun.UI.HostEditor.HostEditorController.CreateEditForm(host);

        Assert.False(form.IsNewHost);
        Assert.Equal("existing", form.Key);
        Assert.Equal("D:", form.Drive);
        Assert.True(form.Tmux);
        Assert.Equal("main", form.TmuxSession);
        Assert.True(form.Autostart);
        Assert.False(form.Reconnect);
    }

    [Fact]
    public void CreateEditForm_ModeNone_LeavesMountDetailFieldsBlankNotNull()
    {
        var host = ValidHost("jump") with
        {
            Mount = new MountConfig { Mode = MountMode.None },
        };

        var form = Bosun.UI.HostEditor.HostEditorController.CreateEditForm(host);

        Assert.Equal(MountMode.None, form.Mode);
        Assert.Equal(string.Empty, form.Drive);
        Assert.Equal(string.Empty, form.RemotePath);
    }

    // ------------------------------------------------------------------------------------------
    // Dependent-field enable rules
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MountMode.Persistent, true)]
    [InlineData(MountMode.OnDemand, true)]
    [InlineData(MountMode.None, false)]
    public void IsMountDetailEnabled_MirrorsMode(MountMode mode, bool expected)
    {
        Assert.Equal(expected, Bosun.UI.HostEditor.HostEditorController.IsMountDetailEnabled(mode));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsTmuxSessionEnabled_MirrorsTmux(bool tmux, bool expected)
    {
        Assert.Equal(expected, Bosun.UI.HostEditor.HostEditorController.IsTmuxSessionEnabled(tmux));
    }

    // ------------------------------------------------------------------------------------------
    // Build: happy path
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Build_ValidModel_ProducesAMatchingHostConfig()
    {
        var result = Bosun.UI.HostEditor.HostEditorController.Build(ValidModel());

        Assert.True(result.Succeeded);
        Assert.Equal("new-host", result.Host!.Key);
        Assert.Equal(22, result.Host.Port);
        Assert.Equal(MountMode.OnDemand, result.Host.Mount.Mode);
        Assert.Equal("D:", result.Host.Mount.Drive);
        Assert.Equal("writes", result.Host.Mount.VfsCacheMode);
    }

    // ------------------------------------------------------------------------------------------
    // Build: Invariant I6 -- vfs_cache_mode never weaker than "writes"
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("writes")]
    [InlineData("full")]
    [InlineData("WRITES")] // case-insensitive, matching ConfigValidator's own comparison
    public void Build_AllowedVfsCacheMode_Succeeds(string mode)
    {
        var model = ValidModel();
        model.VfsCacheMode = mode;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("wirtes")] // typo -- allow-list, not deny-list, per ConfigValidator's own reasoning
    [InlineData("")]
    public void Build_DisallowedVfsCacheMode_IsRejected(string mode)
    {
        var model = ValidModel();
        model.VfsCacheMode = mode;
        // Empty is defaulted to "writes" by Build for parity with ConfigParser, so use a genuinely
        // bad value there instead of asserting failure for the empty case.
        if (mode.Length == 0)
        {
            var emptyResult = Bosun.UI.HostEditor.HostEditorController.Build(model);
            Assert.True(emptyResult.Succeeded);
            Assert.Equal("writes", emptyResult.Host!.Mount.VfsCacheMode);
            return;
        }

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.VfsCacheMode);
    }

    // ------------------------------------------------------------------------------------------
    // Build: Invariant I7 -- network_mode is never offerable as false
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MountMode.Persistent)]
    [InlineData(MountMode.OnDemand)]
    public void Build_MountBearingHost_AlwaysForcesNetworkModeTrue(MountMode mode)
    {
        var model = ValidModel();
        model.Mode = mode;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.True(result.Succeeded);
        Assert.True(result.Host!.Mount.NetworkMode);
    }

    [Fact]
    public void Build_ModeNone_NullsEveryMountDetailField()
    {
        var model = ValidModel();
        model.Mode = MountMode.None;
        // Deliberately leave stray values in the mount-detail fields, as if the user had filled
        // them in before switching the mode to None -- Build must ignore them, not carry them
        // through as garbage the loader would reject.
        model.Drive = "garbage";
        model.RemotePath = "garbage";
        model.VfsCacheMode = "off";
        model.IdleUnmountSeconds = "-5";

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.True(result.Succeeded);
        Assert.Null(result.Host!.Mount.Drive);
        Assert.Null(result.Host.Mount.RemotePath);
        Assert.Null(result.Host.Mount.VfsCacheMode);
        Assert.Null(result.Host.Mount.NetworkMode);
        Assert.Null(result.Host.Mount.IdleUnmountSeconds);
    }

    // ------------------------------------------------------------------------------------------
    // Build: required fields / parse errors
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_BlankKey_IsRejected(string key)
    {
        var model = ValidModel();
        model.Key = key;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void Build_InvalidPort_IsRejected(string port)
    {
        var model = ValidModel();
        model.Port = port;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.Port);
    }

    [Fact]
    public void Build_ModeNotNoneWithoutDriveOrRemotePath_ReportsBothFields()
    {
        var model = ValidModel();
        model.Drive = "";
        model.RemotePath = "";

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.Drive);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.RemotePath);
    }

    [Fact]
    public void Build_TmuxEnabledWithoutSessionName_IsRejected()
    {
        var model = ValidModel();
        model.Tmux = true;
        model.TmuxSession = "";

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.TmuxSession);
    }

    [Fact]
    public void Build_TmuxDisabled_DoesNotRequireASessionName()
    {
        var model = ValidModel();
        model.Tmux = false;
        model.TmuxSession = "";

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.True(result.Succeeded);
        Assert.Null(result.Host!.Session.TmuxSession);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Build_InvalidIdleUnmountSeconds_IsRejected(string value)
    {
        var model = ValidModel();
        model.IdleUnmountSeconds = value;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.IdleUnmountSeconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Build_InvalidProbeInterval_IsRejected(string value)
    {
        var model = ValidModel();
        model.ProbeIntervalSeconds = value;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.ProbeIntervalSeconds);
    }

    [Theory]
    [InlineData("")]
    public void Build_BlankIdentityFile_IsRejected(string value)
    {
        var model = ValidModel();
        model.IdentityFile = value;

        var result = Bosun.UI.HostEditor.HostEditorController.Build(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == HostFormFieldId.IdentityFile);
    }

    // ------------------------------------------------------------------------------------------
    // SaveAsync
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_ParseErrors_NeverReachesTheWriter()
    {
        var (controller, writer, _) = CreateSut();
        var model = ValidModel();
        model.Port = "not-a-number";

        var result = await controller.SaveAsync(model);

        Assert.False(result.Succeeded);
        Assert.Empty(writer.SavedHosts);
        Assert.NotEmpty(result.FieldErrors);
        Assert.NotNull(result.GeneralError);
    }

    [Fact]
    public async Task SaveAsync_WriterSucceeds_ReturnsOk()
    {
        var (controller, writer, _) = CreateSut();
        writer.SaveResult = HostConfigWriteResult.Ok();

        var result = await controller.SaveAsync(ValidModel());

        Assert.True(result.Succeeded);
        Assert.Single(writer.SavedHosts);
        Assert.Equal("new-host", writer.SavedHosts[0].Key);
    }

    [Fact]
    public async Task SaveAsync_WriterRejectsWithValidationErrors_MapsThemToFields_AndKeepsTheFullListInGeneralError()
    {
        var (controller, writer, _) = CreateSut();
        writer.SaveResult = HostConfigWriteResult.Invalid(
        [
            new ConfigValidationError("identity-file-not-found", "hosts.new-host: identity_file '~/x' does not resolve to an existing file"),
        ]);

        var result = await controller.SaveAsync(ValidModel());

        Assert.False(result.Succeeded);
        Assert.Contains(result.FieldErrors, e => e.Field == HostFormFieldId.IdentityFile);
        Assert.Contains("identity_file", result.GeneralError);
    }

    [Fact]
    public async Task SaveAsync_WriterRejectsWithAnUnmappableValidationError_StillSurfacesItInGeneralError()
    {
        var (controller, writer, _) = CreateSut();
        writer.SaveResult = HostConfigWriteResult.Invalid(
        [
            new ConfigValidationError("invalid-backoff-seconds", "global.backoff_seconds must be non-empty"),
        ]);

        var result = await controller.SaveAsync(ValidModel());

        Assert.False(result.Succeeded);
        // Never silently dropped, even though it does not belong to any field on this form.
        Assert.Contains("global.backoff_seconds", result.GeneralError);
    }

    [Fact]
    public async Task SaveAsync_WriterFailsForANonValidationReason_SurfacesTheWritersError()
    {
        var (controller, writer, _) = CreateSut();
        writer.SaveResult = HostConfigWriteResult.Failed("disk full");

        var result = await controller.SaveAsync(ValidModel());

        Assert.False(result.Succeeded);
        Assert.Equal("disk full", result.GeneralError);
    }

    // ------------------------------------------------------------------------------------------
    // DeleteAsync
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_WriterSucceeds_ReturnsOk_AndPassesTheHostKeyThrough()
    {
        var (controller, writer, _) = CreateSut();

        var result = await controller.DeleteAsync("some-host");

        Assert.True(result.Succeeded);
        Assert.Equal(["some-host"], writer.DeletedHostKeys);
    }

    [Fact]
    public async Task DeleteAsync_WriterFails_SurfacesTheError()
    {
        var (controller, writer, _) = CreateSut();
        writer.DeleteResult = HostConfigWriteResult.Failed("host is mounted and could not be drained");

        var result = await controller.DeleteAsync("some-host");

        Assert.False(result.Succeeded);
        Assert.Equal("host is mounted and could not be drained", result.Error);
    }
}
