using Bosun.Configuration;
using Bosun.UI.HostEditor;

namespace Bosun.Tests.UI.HostEditor;

/// <summary>
/// <see cref="HostEditorController.BuildDriveLetterOptions"/> and the same "who claims a letter"
/// rule as <see cref="ConfigValidator"/>. bs-5bk item 2 (maintainer decision 2026-11-01): a
/// <c>mode = "none"</c> host mounts nothing and claims no letter, so the picker must not offer it
/// as "already used" when the validator would accept the letter. Pure logic; no machine state.
/// </summary>
public sealed class DriveLetterPickerTests
{
    private static readonly IReadOnlySet<string> NothingInUse = new HashSet<string>();

    private static HostConfig Host(string key, MountMode mode, string? drive) => new()
    {
        Key = key,
        DisplayName = $"Display {key}",
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someone",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = new MountConfig
        {
            Mode = mode,
            Drive = drive,
            RemotePath = mode == MountMode.None ? null : "/",
            VfsCacheMode = "writes",
            NetworkMode = true,
            IdleUnmountSeconds = 0,
        },
        Session = new SessionConfig(),
        Probe = new ProbeConfig { IntervalSeconds = 60, DeepProbe = true },
    };

    private static BosunConfig ConfigWith(params HostConfig[] hosts) => new()
    {
        Global = new GlobalConfig(),
        Hosts = hosts.ToDictionary(h => h.Key),
    };

    private static DriveLetterOption Option(IReadOnlyList<DriveLetterOption> options, string letter) =>
        options.Single(o => o.Letter == letter);

    /// <summary>
    /// <b>Protects:</b> picker and validator agree. A none-mode host's stale N: is selectable.
    /// <b>Catches:</b> the picker still treating it as a claim, which would mark N: unavailable
    /// ("already used by ...") for the NAS even though the config validates.
    /// </summary>
    [Fact]
    public void A_letter_left_on_a_none_mode_host_is_selectable_for_another_host()
    {
        var config = ConfigWith(Host("jump", MountMode.None, "N:"));

        var options = HostEditorController.BuildDriveLetterOptions(config, NothingInUse, editingHostKey: null);

        var n = Option(options, "N:");
        Assert.True(n.IsAvailable);
        Assert.DoesNotContain("already used", n.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Protects:</b> real claims are still hard conflicts, and a none-mode host sharing the
    /// letter does not hide or double-count them.
    /// <b>Catches:</b> an exclusion that drops the filter for mounting modes, and a
    /// <c>ToDictionary</c> duplicate-key exception when a none-mode and a mounting host share N:.
    /// </summary>
    [Theory]
    [InlineData(MountMode.Persistent)]
    [InlineData(MountMode.OnDemand)]
    public void A_letter_claimed_by_a_mounting_host_is_unavailable_even_if_a_none_mode_host_also_names_it(MountMode mode)
    {
        var config = ConfigWith(
            Host("jump", MountMode.None, "N:"),
            Host("nas", mode, "N:"));

        var options = HostEditorController.BuildDriveLetterOptions(config, NothingInUse, editingHostKey: null);

        var n = Option(options, "N:");
        Assert.False(n.IsAvailable);
        Assert.Contains("Display nas", n.Label, StringComparison.Ordinal);
    }
}
