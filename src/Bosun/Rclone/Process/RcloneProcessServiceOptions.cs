namespace Bosun.Rclone.Process;

/// <summary>Options for <see cref="RcloneProcessService"/>. Always built explicitly from
/// <c>GlobalConfig</c> by the caller (never read from <c>IHostConfigStore</c> directly by the
/// service itself) so the service stays trivially testable with plain values -- the same
/// separation <c>HostProbe</c> uses (it takes primitives per call, not a config store).</summary>
public sealed record RcloneProcessServiceOptions
{
    /// <summary>Name or path of the rclone executable. "rclone" (resolved via PATH) in
    /// production; tests override with a fake launcher instead of a real path.</summary>
    public string ExecutablePath { get; init; } = "rclone";

    /// <summary>Loopback port to bind <c>rclone rcd</c> to. NEVER a non-loopback interface --
    /// even though the rc API now requires Basic auth with a random per-launch credential
    /// (bs-ard; see <see cref="RcloneProcessService.BuildStartInfo"/>), a loopback-only bind
    /// remains the point of defence against anything off-box, and a LAN-reachable bind was never
    /// the design.</summary>
    public required int RcloneRcPort { get; init; }

    /// <summary>Passed as <c>--config</c> so <c>rclone rcd</c> reads/writes the same
    /// <c>rclone.conf</c> <see cref="Rclone.RcloneRemoteProvisioner"/> provisions into.</summary>
    public required string RcloneConfigPath { get; init; }

    /// <summary>How long to wait for <c>core/version</c> to respond after starting the process
    /// before treating the start attempt as failed (<see cref="RcloneProcessFaultKind.HealthCheckFailed"/>).</summary>
    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Interval between <c>core/version</c> poll attempts while waiting for health.</summary>
    public TimeSpan HealthCheckPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Delay before each restart attempt after an unexpected exit or a failed start
    /// (other than <see cref="RcloneProcessFaultKind.ExecutableNotFound"/>, which does not
    /// retry at all -- see <see cref="RcloneProcessService"/>'s remarks).</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="RcloneProcessService.StopAsync"/> waits for the killed
    /// process to confirm exit before giving up and returning anyway.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
