namespace Bosun.Configuration;

/// <summary>
/// The values a new host starts with, so adding one in the window is a few fields rather than a
/// blank form (ADR-019: "with defaults pre-filled").
/// </summary>
/// <remarks>
/// Pure, and deliberately separate from the UI: the defaults are a configuration concern with
/// invariants attached, not presentation. Every choice below is either an invariant (I6, I7) or a
/// reasoned starting point, and each is commented with which.
/// </remarks>
public static class NewHostDefaults
{
    /// <summary>Drive letters Bosun may assign. A–C are reserved by Windows convention and are
    /// rejected by <see cref="ConfigValidator"/> anyway.</summary>
    private const string AssignableDriveLetters = "DEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Builds a host pre-filled with sensible values, given the configuration it will
    /// join (so the drive letter it picks does not collide with an existing host).</summary>
    /// <param name="current">The configuration this host is being added to. Used only to avoid
    /// collisions; never mutated.</param>
    /// <param name="hostKey">The TOML key, which is also the <c>~/.ssh/config</c> Host alias the
    /// generated Terminal profile will run (<c>ssh &lt;key&gt;</c>, ADR-013).</param>
    public static HostConfig Create(BosunConfig current, string hostKey)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKey);

        return new HostConfig
        {
            Key = hostKey,
            DisplayName = hostKey,
            Hostname = string.Empty,
            Port = 22,
            User = string.Empty,
            IdentityFile = string.Empty,

            Mount = new MountConfig
            {
                // ON-DEMAND, not persistent, deliberately. A newly added host has never been
                // reached; persistent would have Bosun try to mount it the moment it is saved.
                // On-demand means nothing happens until the user asks, so a wrong hostname or a
                // missing key is a click that fails rather than a drive letter appearing and
                // disappearing at startup.
                Mode = MountMode.OnDemand,
                Drive = FirstFreeDriveLetter(current),
                RemotePath = string.Empty,

                // Invariant I6: "writes" is the floor. rclone's own default is "off", which is
                // the exact value I6 forbids, so this must be set explicitly and must never be
                // offered as weaker in the UI.
                VfsCacheMode = MountConfig.DefaultVfsCacheMode,

                // Invariant I7: always. Presenting an SFTP mount as a fixed disk produces
                // pathological Explorer behaviour.
                NetworkMode = true,

                // 0 = never idle-unmount. Only meaningful for on-demand hosts, and a surprising
                // default to make non-zero: a drive vanishing while the user still wants it is
                // worse than one that lingers.
                IdleUnmountSeconds = 0,
            },

            Session = new SessionConfig
            {
                Autostart = false,
                // Retry on SSH's connection-dropped exit code, so a brief blip reconnects the tab
                // instead of dumping the user at a dead prompt.
                Reconnect = true,
                Tmux = false,
                TmuxSession = null,
                TabColor = "#2D5F3F",
                ColorScheme = "Campbell",
            },

            Probe = new ProbeConfig
            {
                IntervalSeconds = 60,
                DeepProbe = true,
            },
        };
    }

    /// <summary>The first drive letter in D–Z that no host in <paramref name="current"/> claims.
    /// Returns <see langword="null"/> when every letter is taken, which the caller must surface
    /// rather than silently reusing one — a drive collision is never auto-resolved
    /// (<see cref="ConfigValidator"/>).</summary>
    /// <remarks>
    /// Deliberately considers only Bosun's own configuration, not drives currently mounted on the
    /// machine. A letter used by a USB stick today may be free tomorrow, and a config that changes
    /// meaning depending on what happened to be plugged in when the host was added would be worse
    /// than one the user has to correct once.
    /// </remarks>
    public static string? FirstFreeDriveLetter(BosunConfig current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var claimed = current.Hosts.Values
            .Select(h => h.Mount.Drive)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.TrimEnd(':').ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var letter in AssignableDriveLetters)
        {
            if (!claimed.Contains(letter.ToString()))
            {
                return $"{letter}:";
            }
        }

        return null;
    }
}
