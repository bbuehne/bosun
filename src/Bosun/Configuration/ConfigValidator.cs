using System.IO;
using System.Text.RegularExpressions;

namespace Bosun.Configuration;

/// <summary>
/// A single, locatable validation failure. <see cref="Rule"/> identifies which
/// docs/CONFIG-SCHEMA.md rule fired (stable, kebab-case, safe to assert on in tests);
/// <see cref="Message"/> is the human-readable, locatable text (which host, which field, what
/// was wrong).
/// </summary>
public sealed record ConfigValidationError(string Rule, string Message)
{
    public override string ToString() => Message;
}

/// <summary>Result of <see cref="ConfigValidator.Validate"/>. Never partial: either
/// <see cref="Errors"/> is empty and the config is good to adopt whole, or it is not and the
/// whole config must be rejected (docs/ARCHITECTURE.md §6).</summary>
public sealed record ConfigValidationResult(IReadOnlyList<ConfigValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static readonly ConfigValidationResult Valid = new([]);
}

/// <summary>
/// Every rule in docs/CONFIG-SCHEMA.md's "Validation rules" section (bs-c0g). A pure function
/// over the already-bound <see cref="BosunConfig"/> (see <see cref="ConfigParser"/>) — the only
/// I/O is the identity-file existence check, and that is injected so tests never depend on the
/// developer's real <c>~/.ssh</c> contents.
/// </summary>
public static class ConfigValidator
{
    // \A and \z, NOT ^ and $. docs/CONFIG-SCHEMA.md writes the rule as ^[D-Z]:$, but in .NET `$`
    // also matches immediately before a trailing newline, so "D:\n" -- expressible in a TOML basic
    // string -- passed validation with zero errors and would have reached mount/mount verbatim as
    // the mount point. \z anchors to the true end of input. Caught by an independent adversarial
    // test (Validate_DriveWithATrailingNewline_IsRejected); do not "simplify" this back.
    private static readonly Regex DriveLetterPattern = new(@"\A[D-Z]:\z", RegexOptions.Compiled);

    // bs-lrd / bs-mb2: an allow-list, not a deny-list. A deny-list of {off, minimal} let any
    // typo ("wirtes") or any casing rclone itself accepts ("Off", "OFF") straight through
    // validation and into mount/mount, where it only failed once the mount was already
    // attempted. Invariant I6 makes "writes" a correctness floor, so anything not affirmatively
    // known-safe is rejected -- compared case-insensitively, because rclone's own flag parsing
    // does not care about case and neither should we.
    private static readonly IReadOnlySet<string> AllowedVfsCacheModes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "writes", "full" };

    private const int MinValidRcloneRcPort = 1;
    private const int MaxValidRcloneRcPort = 65535;

    /// <summary>
    /// Validates <paramref name="config"/>, returning every rule violation found — never just
    /// the first (docs/CONFIG-SCHEMA.md: "A config violating two rules must report both").
    /// </summary>
    /// <param name="config">The bound config to validate.</param>
    /// <param name="identityFileExists">
    /// Injected existence check for <c>identity_file</c>, called with the path after leading
    /// <c>~</c> expansion. Defaults to a real <see cref="File.Exists(string)"/> check against the
    /// current user's profile when null — production callers may omit it, but no test should:
    /// pass a fake so tests never depend on the developer's real <c>~/.ssh</c> contents.
    /// </param>
    public static ConfigValidationResult Validate(BosunConfig config, Func<string, bool>? identityFileExists = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        identityFileExists ??= DefaultIdentityFileExists;

        var errors = new List<ConfigValidationError>();

        ValidateGlobal(config.Global, errors);
        ValidateDuplicateDisplayNames(config.Hosts, errors);
        ValidateDriveCollisions(config.Hosts, errors);

        foreach (var host in config.Hosts.Values)
        {
            ValidateHost(host, identityFileExists, errors);
        }

        return errors.Count == 0 ? ConfigValidationResult.Valid : new ConfigValidationResult(errors);
    }

    private static void ValidateGlobal(GlobalConfig global, List<ConfigValidationError> errors)
    {
        if (global.BackoffSeconds.Count == 0 || global.BackoffSeconds.Any(v => v <= 0))
        {
            errors.Add(new ConfigValidationError(
                "invalid-backoff-seconds",
                $"global.backoff_seconds must be non-empty and contain only positive values (got [{string.Join(", ", global.BackoffSeconds)}])"));
        }

        // ADR-011: absent already defaulted to 60 by ConfigParser. By the time it reaches here
        // it is never "absent" -- only an explicit zero/negative value is rejected.
        if (global.MountedProbeIntervalSeconds <= 0)
        {
            errors.Add(new ConfigValidationError(
                "invalid-mounted-probe-interval",
                $"global.mounted_probe_interval_seconds must be positive (got {global.MountedProbeIntervalSeconds}) -- a mounted host must always have a probe cadence (ADR-011)"));
        }

        // bs-z3y: the same shape of hole ADR-011 closed for mounted_probe_interval_seconds.
        // Depending on how MountSupervisor's ">=" comparison reads, 0 means either "unmount on
        // the very next probe tick" or -- the actually observed direction -- "never unmount",
        // because ConsecutiveMountedFailures never reaches a threshold of 0 by incrementing from
        // 0. Either way it is a direct Invariant I2 violation, so anything below 1 is rejected
        // outright rather than left to the runtime clamp in MountSupervisor.
        if (global.FailuresBeforeUnmount < 1)
        {
            errors.Add(new ConfigValidationError(
                "invalid-failures-before-unmount",
                $"global.failures_before_unmount must be at least 1 (got {global.FailuresBeforeUnmount}) -- below 1 a mounted host is never unmounted on probe failure (Invariant I2)"));
        }

        if (global.ProbeTimeoutSeconds <= 0)
        {
            errors.Add(new ConfigValidationError(
                "invalid-probe-timeout",
                $"global.probe_timeout_seconds must be positive (got {global.ProbeTimeoutSeconds})"));
        }

        if (global.RcloneRcPort is < MinValidRcloneRcPort or > MaxValidRcloneRcPort)
        {
            errors.Add(new ConfigValidationError(
                "invalid-rclone-rc-port",
                $"global.rclone_rc_port must be between {MinValidRcloneRcPort} and {MaxValidRcloneRcPort} (got {global.RcloneRcPort})"));
        }

        if (global.SuspendUnmountTimeoutSeconds <= 0)
        {
            errors.Add(new ConfigValidationError(
                "invalid-suspend-unmount-timeout",
                $"global.suspend_unmount_timeout_seconds must be positive (got {global.SuspendUnmountTimeoutSeconds}) -- Invariant I8 requires unmounting before suspend within this window"));
        }
    }

    private static void ValidateDuplicateDisplayNames(
        IReadOnlyDictionary<string, HostConfig> hosts,
        List<ConfigValidationError> errors)
    {
        // bs-5bk: case-insensitive. E7 emits Windows Terminal profiles named from display_name,
        // and `wt -p` has undocumented tie-breaking on a collision -- "Prod" and "prod" are the
        // same hazard as an exact duplicate, just spelled differently.
        foreach (var group in hosts.Values.GroupBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                var keys = string.Join(", ", group.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal));
                errors.Add(new ConfigValidationError(
                    "duplicate-display-name",
                    $"display_name '{group.Key}' is used by more than one host ({keys}) -- profile launching (wt -p) is not deterministic on a collision (compared case-insensitively)"));
            }
        }
    }

    private static void ValidateDriveCollisions(
        IReadOnlyDictionary<string, HostConfig> hosts,
        List<ConfigValidationError> errors)
    {
        foreach (var group in hosts.Values
                     .Where(h => h.Mount.Drive is not null)
                     .GroupBy(h => h.Mount.Drive, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                var keys = string.Join(", ", group.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal));
                errors.Add(new ConfigValidationError(
                    "drive-collision",
                    $"drive '{group.Key}' is claimed by more than one host ({keys}) -- drive collisions are never auto-resolved"));
            }
        }
    }

    private static void ValidateHost(
        HostConfig host,
        Func<string, bool> identityFileExists,
        List<ConfigValidationError> errors)
    {
        if (host.Mount.Mode != MountMode.None)
        {
            if (string.IsNullOrEmpty(host.Mount.Drive) || string.IsNullOrEmpty(host.Mount.RemotePath))
            {
                errors.Add(new ConfigValidationError(
                    "mount-missing-drive-or-remote-path",
                    $"hosts.{host.Key}: mount.mode = '{ToTomlValue(host.Mount.Mode)}' requires both mount.drive and mount.remote_path"));
            }
        }

        if (host.Mount.Drive is not null && !DriveLetterPattern.IsMatch(host.Mount.Drive))
        {
            errors.Add(new ConfigValidationError(
                "invalid-drive-letter",
                $"hosts.{host.Key}: mount.drive '{host.Mount.Drive}' does not match ^[D-Z]:$ (A-C reserved)"));
        }

        // bs-lrd / bs-mb2: allow-list, not deny-list -- absent (null) is not an error here; a
        // missing field is defaulted to "writes" by ConfigParser (the config layer), so nothing
        // downstream ever sees a null. Only an explicitly-supplied, unrecognised value fires.
        if (host.Mount.VfsCacheMode is not null && !AllowedVfsCacheModes.Contains(host.Mount.VfsCacheMode))
        {
            errors.Add(new ConfigValidationError(
                "invalid-vfs-cache-mode",
                $"hosts.{host.Key}: mount.vfs_cache_mode '{host.Mount.VfsCacheMode}' is not one of {{writes, full}} -- 'writes' is the floor (Invariant I6)"));
        }

        // bs-b8t: I7 requires --network-mode on every mount. The field exists "for debugging
        // only" per docs/CONFIG-SCHEMA.md, but an explicit `false` is an authoring mistake that
        // previously passed validation silently and was only ever caught by RcloneClient
        // hardcoding true into the mount/mount body -- the config the user wrote and the mount
        // that actually happened silently disagreed. Reject it here instead.
        if (host.Mount.NetworkMode == false)
        {
            errors.Add(new ConfigValidationError(
                "invalid-network-mode",
                $"hosts.{host.Key}: mount.network_mode must be true (Invariant I7) -- got false"));
        }

        if (host.Mount.IdleUnmountSeconds is < 0)
        {
            errors.Add(new ConfigValidationError(
                "negative-idle-unmount-seconds",
                $"hosts.{host.Key}: mount.idle_unmount_seconds must not be negative (got {host.Mount.IdleUnmountSeconds})"));
        }

        if (!identityFileExists(ExpandHome(host.IdentityFile)))
        {
            errors.Add(new ConfigValidationError(
                "identity-file-not-found",
                $"hosts.{host.Key}: identity_file '{host.IdentityFile}' does not resolve to an existing file"));
        }

        if (host.Probe.IntervalSeconds < 0)
        {
            errors.Add(new ConfigValidationError(
                "negative-probe-interval",
                $"hosts.{host.Key}: probe.interval_seconds must not be negative (got {host.Probe.IntervalSeconds})"));
        }
    }

    private static string ToTomlValue(MountMode mode) => mode switch
    {
        MountMode.Persistent => "persistent",
        MountMode.OnDemand => "on-demand",
        MountMode.None => "none",
        _ => mode.ToString(),
    };

    /// <summary>Expands a leading <c>~</c> (as <c>~/...</c> or bare <c>~</c>) to the current
    /// user's profile directory. TOML text and the bound model keep the literal, unexpanded
    /// path (docs/CONFIG-SCHEMA.md examples write <c>~/.ssh/id_ed25519</c>); expansion is purely
    /// an implementation detail of resolving the existence check.</summary>
    internal static string ExpandHome(string path)
    {
        if (path.Length == 0 || path[0] != '~')
        {
            return path;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1)
        {
            return userProfile;
        }

        return path[1] is '/' or '\\'
            ? Path.Combine(userProfile, path[2..])
            : path;
    }

    private static bool DefaultIdentityFileExists(string path) => File.Exists(path);
}
