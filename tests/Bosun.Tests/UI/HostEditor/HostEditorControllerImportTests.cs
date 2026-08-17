using Bosun.Configuration;
using Bosun.Import;
using Bosun.Tests.UI.HostEditor.Fakes;
using Bosun.UI.HostEditor;

namespace Bosun.Tests.UI.HostEditor;

/// <summary>
/// <see cref="HostEditorController.CreateImportedHostForm"/> (bs-ww9.9, ADR-019): overlaying a
/// successful <see cref="BitviseImportResult"/> onto the same <see cref="NewHostDefaults"/>
/// baseline <see cref="HostEditorController.CreateNewHostForm"/> uses. Never constructs a WPF
/// <see cref="System.Windows.Window"/> -- same fakes as <see cref="HostEditorControllerTests"/>.
/// </summary>
public sealed class HostEditorControllerImportTests
{
    private static BosunConfig EmptyConfig() => new()
    {
        Global = new GlobalConfig(),
        Hosts = new Dictionary<string, HostConfig>(),
    };

    private static Bosun.UI.HostEditor.HostEditorController CreateSut(BosunConfig? config = null)
    {
        var writer = new FakeHostConfigWriter();
        var store = new FakeHostConfigStore(config ?? EmptyConfig());
        return new Bosun.UI.HostEditor.HostEditorController(writer, store);
    }

    [Fact]
    public void CreateImportedHostForm_OverlaysHostnameUsernameAndPort_OnTheNewHostDefaults()
    {
        var controller = CreateSut();
        var imported = BitviseImportResult.Ok("traininggrounds.local", "bbuehne", 2222, "Tunnelier 9.51");

        var form = controller.CreateImportedHostForm("traininggrounds", imported);

        Assert.Equal("traininggrounds", form.Key);
        Assert.True(form.IsNewHost);
        Assert.Equal("traininggrounds.local", form.Hostname);
        Assert.Equal("bbuehne", form.User);
        Assert.Equal("2222", form.Port);
    }

    [Fact]
    public void CreateImportedHostForm_DefaultsPort22_WhenImportDidNotOverrideIt()
    {
        var controller = CreateSut();
        var imported = BitviseImportResult.Ok("mccharm.com", "ubuntu", 22, null);

        var form = controller.CreateImportedHostForm("mccharm", imported);

        Assert.Equal("22", form.Port);
    }

    [Fact]
    public void CreateImportedHostForm_LeavesUserAtDefault_WhenImportFoundNoUsername()
    {
        var controller = CreateSut();
        var imported = BitviseImportResult.Ok("mccharm.com", username: null, port: 22, detectedVersion: null);

        var form = controller.CreateImportedHostForm("mccharm", imported);

        // NewHostDefaults.Create leaves User empty -- an import that found no username must not
        // invent one.
        Assert.Equal(string.Empty, form.User);
    }

    [Fact]
    public void CreateImportedHostForm_NeverPopulatesIdentityFile_EvenThoughItIsRequiredToSave()
    {
        // ADR-019's "import cannot supply the key": there is no field on BitviseImportResult that
        // could carry one, because Bitvise never exposed one to extract. This asserts the negative
        // space -- the identity file must stay at NewHostDefaults' empty default -- since a
        // regression here would silently fabricate a path that does not exist.
        var controller = CreateSut();
        var imported = BitviseImportResult.Ok("mccharm.com", "ubuntu", 22, null);

        var form = controller.CreateImportedHostForm("mccharm", imported);

        Assert.Equal(string.Empty, form.IdentityFile);
    }

    [Fact]
    public void CreateImportedHostForm_StillAppliesOrdinaryNewHostDefaults()
    {
        var controller = CreateSut(new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>
            {
                ["existing"] = new HostConfig
                {
                    Key = "existing",
                    DisplayName = "existing",
                    Hostname = "example.internal",
                    Port = 22,
                    User = "someone",
                    IdentityFile = "~/.ssh/id_ed25519",
                    Mount = new MountConfig { Mode = MountMode.OnDemand, Drive = "D:", RemotePath = "/", VfsCacheMode = "writes", NetworkMode = true, IdleUnmountSeconds = 0 },
                    Session = new SessionConfig { Autostart = false, Reconnect = true, Tmux = false, TabColor = "#2D5F3F", ColorScheme = "Campbell" },
                    Probe = new ProbeConfig { IntervalSeconds = 60, DeepProbe = true },
                },
            },
        });
        var imported = BitviseImportResult.Ok("mccharm.com", "ubuntu", 22, null);

        var form = controller.CreateImportedHostForm("mccharm", imported);

        // On-demand, not persistent (NewHostDefaults' reasoning applies just as much to an
        // imported host -- an imported hostname has never been reached by Bosun either), and the
        // first free drive letter skips the one "existing" already claims.
        Assert.Equal(MountMode.OnDemand, form.Mode);
        Assert.Equal("E:", form.Drive);
    }

    [Fact]
    public void CreateImportedHostForm_Throws_WhenTheImportDidNotSucceed()
    {
        var controller = CreateSut();
        var imported = BitviseImportResult.Failed("no hostname found");

        Assert.Throws<ArgumentException>(() => controller.CreateImportedHostForm("whatever", imported));
    }

    [Fact]
    public void CreateImportedHostForm_ThrowsArgumentNullException_ForNullResult()
    {
        var controller = CreateSut();

        Assert.Throws<ArgumentNullException>(() => controller.CreateImportedHostForm("whatever", null!));
    }
}
