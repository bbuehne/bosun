using Bosun.Configuration;

namespace Bosun.Tests.Supervisor.Support;

/// <summary>Small builders for the <see cref="HostConfig"/>/<see cref="GlobalConfig"/> shapes
/// <c>MountSupervisor</c> tests need repeatedly, following the same direct-object-construction
/// style as <c>ConfigValidatorTests.ValidHost</c> rather than introducing a fluent builder.</summary>
internal static class HostFixtures
{
    public static HostConfig Persistent(
        string key,
        int probeIntervalSeconds = 60,
        string drive = "P:",
        int? idleUnmountSeconds = null) => new()
    {
        Key = key,
        DisplayName = key,
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = new MountConfig
        {
            Mode = MountMode.Persistent,
            Drive = drive,
            RemotePath = "/srv/share",
            VfsCacheMode = "writes",
            NetworkMode = true,
            IdleUnmountSeconds = idleUnmountSeconds,
        },
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = false,
            Tmux = false,
            TabColor = "#000000",
            ColorScheme = "Campbell",
        },
        Probe = new ProbeConfig { IntervalSeconds = probeIntervalSeconds, DeepProbe = true },
    };

    public static HostConfig OnDemand(
        string key,
        int probeIntervalSeconds = 0,
        int idleUnmountSeconds = 0,
        string drive = "Q:")
    {
        var host = Persistent(key, probeIntervalSeconds, drive, idleUnmountSeconds);
        return host with { Mount = host.Mount with { Mode = MountMode.OnDemand } };
    }

    public static HostConfig None(string key)
    {
        var host = Persistent(key);
        return host with { Mount = new MountConfig { Mode = MountMode.None } };
    }

    public static GlobalConfig Global(
        int probeTimeoutSeconds = 5,
        int failuresBeforeUnmount = 3,
        IReadOnlyList<int>? backoffSeconds = null,
        int mountedProbeIntervalSeconds = 60,
        int suspendUnmountTimeoutSeconds = 8) => new()
    {
        ProbeTimeoutSeconds = probeTimeoutSeconds,
        FailuresBeforeUnmount = failuresBeforeUnmount,
        BackoffSeconds = backoffSeconds ?? GlobalConfig.DefaultBackoffSeconds,
        MountedProbeIntervalSeconds = mountedProbeIntervalSeconds,
        SuspendUnmountTimeoutSeconds = suspendUnmountTimeoutSeconds,
    };

    public static BosunConfig Build(GlobalConfig global, params HostConfig[] hostList) => new()
    {
        Global = global,
        Hosts = hostList.ToDictionary(h => h.Key, StringComparer.Ordinal),
    };
}
