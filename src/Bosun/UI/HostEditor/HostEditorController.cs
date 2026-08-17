using System.Globalization;
using Bosun.Configuration;
using Bosun.Supervisor;
using Microsoft.Extensions.Logging;

namespace Bosun.UI.HostEditor;

/// <summary>
/// The logic behind the host-editor form (bs-ww9.8, ADR-019): building a <see cref="HostConfig"/>
/// out of a <see cref="HostFormModel"/>, applying <see cref="NewHostDefaults"/> for a new host,
/// and driving <see cref="IHostConfigWriter"/> for save/delete. Deliberately WPF-free -- follows
/// the same extraction shape as <see cref="MainWindowController"/> and
/// <see cref="Hosting.BootstrapOrchestrator"/> so it is unit-testable without a
/// <see cref="System.Windows.Window"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two layers of validation, on purpose.</b> <see cref="Build"/> catches parse-level problems
/// (an unparsable port, a missing required field) before anything is sent anywhere.
/// <see cref="SaveAsync"/> then hands the result to <see cref="IHostConfigWriter.SaveHostAsync"/>,
/// which re-validates the WHOLE resulting config with <see cref="ConfigValidator"/> -- the
/// authoritative gate, because it alone knows about every other host (duplicate display names,
/// drive collisions) and about the filesystem (identity file existence). This class never
/// second-guesses that second layer; it only maps its result back onto fields.
/// </para>
/// <para>
/// <b>I6 / I7 are enforced here, not merely displayed.</b> <see cref="AllowedVfsCacheModes"/> is
/// the closed set <c>HostEditorWindow</c>'s combo box must be built from -- never a free-text
/// field -- and <see cref="Build"/> hardcodes <c>network_mode = true</c> for every mount-bearing
/// host regardless of anything on <see cref="HostFormModel"/> (which has no property that could
/// carry <see langword="false"/> in the first place). Both are belt-and-braces: even a caller that
/// somehow got \"off\" into <see cref="HostFormModel.VfsCacheMode"/> gets a parse-level error here,
/// before <see cref="ConfigValidator"/> ever sees it.
/// </para>
/// </remarks>
public sealed class HostEditorController
{
    /// <summary>The only values <c>HostEditorWindow</c>'s vfs-cache-mode control may offer.
    /// Invariant I6 makes <c>"writes"</c> the floor -- rclone's own default is <c>"off"</c>, the
    /// exact value I6 forbids -- so nothing weaker is ever presented as a choice. Mirrors
    /// <see cref="ConfigValidator"/>'s allow-list; kept as an independent constant here (rather
    /// than reflecting into that private set) because the two serve different purposes: this one
    /// bounds what the UI offers, that one is the authoritative gate on what is accepted.</summary>
    public static readonly IReadOnlyList<string> AllowedVfsCacheModes = ["writes", "full"];

    private readonly IHostConfigWriter _writer;
    private readonly IHostConfigStore _configStore;

    /// <summary>Used only to unmount a host before deleting it (Invariant I2). Optional:
    /// null skips the drain, which is correct for tests that are not exercising delete.</summary>
    private readonly IMountSupervisor? _supervisor;
    private readonly ILogger<HostEditorController>? _logger;

    public HostEditorController(
        IHostConfigWriter writer,
        IHostConfigStore configStore,
        IMountSupervisor? supervisor = null,
        ILogger<HostEditorController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(configStore);

        _writer = writer;
        _configStore = configStore;
        _supervisor = supervisor;
        _logger = logger;
    }

    /// <summary>True if <paramref name="key"/> already names a host in the current config --
    /// used to refuse a duplicate key before the user fills in the rest of a new host's form.</summary>
    public bool KeyAlreadyExists(string key) => _configStore.Current.Hosts.ContainsKey(key);

    /// <summary>Builds a new-host form pre-filled from <see cref="NewHostDefaults.Create"/> --
    /// "don't reinvent them" is the brief's explicit instruction, so every default value here
    /// traces back to that method, never to a literal in this class.</summary>
    public HostFormModel CreateNewHostForm(string hostKey)
    {
        var defaults = NewHostDefaults.Create(_configStore.Current, hostKey);
        return ToFormModel(defaults, isNewHost: true);
    }

    /// <summary>Builds an edit form from an already-saved host.</summary>
    public static HostFormModel CreateEditForm(HostConfig existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return ToFormModel(existing, isNewHost: false);
    }

    private static HostFormModel ToFormModel(HostConfig host, bool isNewHost) => new()
    {
        Key = host.Key,
        IsNewHost = isNewHost,
        DisplayName = host.DisplayName,
        Hostname = host.Hostname,
        Port = host.Port.ToString(CultureInfo.InvariantCulture),
        User = host.User,
        IdentityFile = host.IdentityFile,
        Mode = host.Mount.Mode,
        Drive = host.Mount.Drive ?? string.Empty,
        RemotePath = host.Mount.RemotePath ?? string.Empty,
        VfsCacheMode = host.Mount.VfsCacheMode ?? MountConfig.DefaultVfsCacheMode,
        IdleUnmountSeconds = (host.Mount.IdleUnmountSeconds ?? 0).ToString(CultureInfo.InvariantCulture),
        Autostart = host.Session.Autostart,
        Reconnect = host.Session.Reconnect,
        Tmux = host.Session.Tmux,
        TmuxSession = host.Session.TmuxSession ?? string.Empty,
        TabColor = host.Session.TabColor,
        ColorScheme = host.Session.ColorScheme,
        ProbeIntervalSeconds = host.Probe.IntervalSeconds.ToString(CultureInfo.InvariantCulture),
        DeepProbe = host.Probe.DeepProbe,
    };

    /// <summary>
    /// The color schemes Windows Terminal ships with, verified against Microsoft Learn's
    /// "Included color schemes" list rather than recalled — Solarized, for instance, is commonly
    /// assumed to be built in and is not.
    /// </summary>
    /// <remarks>
    /// This list is deliberately NOT exhaustive of what the user has available, and the control
    /// bound to it must stay editable. A scheme can also come from the user's own
    /// <c>settings.json</c> or from another app's fragment (the WSL fragment ships "Ubuntu", for
    /// example), and Invariant I5 forbids Bosun reading <c>settings.json</c> to find out. So this
    /// offers the names that are always present and lets anything else be typed.
    /// </remarks>
    public static readonly IReadOnlyList<string> BuiltInColorSchemes =
    [
        "Campbell",
        "Campbell Powershell",
        "Vintage",
        "One Half Dark",
        "One Half Light",
        "Tango Dark",
        "Tango Light",
    ];

    /// <summary>
    /// Builds the drive-letter choices for a host, marking letters that are already taken.
    /// </summary>
    /// <param name="config">Current configuration — used to find letters other hosts have claimed.</param>
    /// <param name="inUseOnMachine">Bare uppercase letters currently mounted on this machine.</param>
    /// <param name="editingHostKey">The host being edited, or <see langword="null"/> for a new
    /// host. Its OWN letter must stay selectable: a mounted host's drive shows as in-use on the
    /// machine precisely because that host is using it, and excluding it would make its current
    /// setting unpickable — the field would silently reset the moment the user opened the form.</param>
    /// <remarks>
    /// Unavailable letters are listed and disabled rather than hidden, because a letter silently
    /// missing from the list reads as a bug. Another Bosun host's claim is a hard conflict
    /// (<see cref="ConfigValidator"/> rejects duplicate drives outright); a letter merely in use on
    /// the machine is advisory, since it may be a USB stick that will be gone tomorrow.
    /// </remarks>
    public static IReadOnlyList<DriveLetterOption> BuildDriveLetterOptions(
        BosunConfig config,
        IReadOnlySet<string> inUseOnMachine,
        string? editingHostKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(inUseOnMachine);

        var claimedByOtherHost = config.Hosts.Values
            .Where(h => editingHostKey is null || !string.Equals(h.Key, editingHostKey, StringComparison.Ordinal))
            .Where(h => !string.IsNullOrWhiteSpace(h.Mount.Drive))
            .ToDictionary(
                h => h.Mount.Drive!.TrimEnd(':').ToUpperInvariant(),
                h => h.DisplayName,
                StringComparer.OrdinalIgnoreCase);

        var ownLetter = editingHostKey is not null
            && config.Hosts.TryGetValue(editingHostKey, out var own)
            && !string.IsNullOrWhiteSpace(own.Mount.Drive)
                ? own.Mount.Drive!.TrimEnd(':').ToUpperInvariant()
                : null;

        var options = new List<DriveLetterOption>();

        foreach (var letter in "DEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var bare = letter.ToString();
            var drive = $"{letter}:";

            if (claimedByOtherHost.TryGetValue(bare, out var otherHost))
            {
                options.Add(new DriveLetterOption
                {
                    Letter = drive,
                    IsAvailable = false,
                    Label = $"{drive}  — already used by {otherHost}",
                });
                continue;
            }

            if (string.Equals(bare, ownLetter, StringComparison.OrdinalIgnoreCase))
            {
                options.Add(new DriveLetterOption
                {
                    Letter = drive,
                    IsAvailable = true,
                    Label = $"{drive}  — this host's current drive",
                });
                continue;
            }

            var busy = inUseOnMachine.Contains(bare);
            options.Add(new DriveLetterOption
            {
                Letter = drive,
                IsAvailable = !busy,
                Label = busy ? $"{drive}  — in use on this PC" : drive,
            });
        }

        return options;
    }

    /// <summary>Whether the mount-detail fields (drive, remote path, vfs cache mode, idle
    /// unmount) should be enabled -- mirrors <see cref="ConfigValidator"/>'s own "required when
    /// mode != none" rule, so the form never lets you fill in fields a <c>none</c>-mode host would
    /// have to carry as null anyway.</summary>
    public static bool IsMountDetailEnabled(MountMode mode) => mode != MountMode.None;

    /// <summary>Whether the tmux-session field should be enabled -- mirrors
    /// <see cref="ConfigValidator"/>'s <c>tmux-requires-session</c> rule.</summary>
    public static bool IsTmuxSessionEnabled(bool tmux) => tmux;

    /// <summary>Parses <paramref name="model"/> into a <see cref="HostConfig"/>, or returns the
    /// client-side errors that stopped it. Never calls the writer -- see <see cref="SaveAsync"/>.</summary>
    public static HostFormBuildResult Build(HostFormModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var errors = new List<HostFormValidationError>();

        var key = model.Key.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add(new HostFormValidationError(
                HostFormFieldId.Key,
                "A short name is required -- an identifier for this host, not a security key. "
                + "Bosun's Terminal profile runs \"ssh <this name>\", so it should match a Host alias in "
                + "~/.ssh/config; the drive mount works either way."));
        }

        var displayName = model.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            errors.Add(new HostFormValidationError(HostFormFieldId.DisplayName, "Display name is required."));
        }

        var hostname = model.Hostname.Trim();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            errors.Add(new HostFormValidationError(HostFormFieldId.Hostname, "Hostname is required."));
        }

        var user = model.User.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            errors.Add(new HostFormValidationError(HostFormFieldId.User, "User is required."));
        }

        var identityFile = model.IdentityFile.Trim();
        if (string.IsNullOrWhiteSpace(identityFile))
        {
            errors.Add(new HostFormValidationError(
                HostFormFieldId.IdentityFile,
                "Identity file is required (Invariant I10 -- key-based authentication only)."));
        }

        if (!int.TryParse(model.Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            errors.Add(new HostFormValidationError(
                HostFormFieldId.Port,
                $"Port must be a whole number between 1 and 65535 (got '{model.Port}')."));
        }

        string? drive = null;
        string? remotePath = null;
        string? vfsCacheMode = null;
        bool? networkMode = null;
        int? idleUnmountSeconds = null;

        if (model.Mode != MountMode.None)
        {
            drive = model.Drive.Trim();
            remotePath = model.RemotePath.Trim();
            if (string.IsNullOrWhiteSpace(drive) || string.IsNullOrWhiteSpace(remotePath))
            {
                errors.Add(new HostFormValidationError(
                    HostFormFieldId.Drive, "Mount mode requires both a drive letter and a remote path."));
                errors.Add(new HostFormValidationError(
                    HostFormFieldId.RemotePath, "Mount mode requires both a drive letter and a remote path."));
            }

            vfsCacheMode = string.IsNullOrWhiteSpace(model.VfsCacheMode)
                ? MountConfig.DefaultVfsCacheMode
                : model.VfsCacheMode.Trim();
            if (!AllowedVfsCacheModes.Contains(vfsCacheMode, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new HostFormValidationError(
                    HostFormFieldId.VfsCacheMode,
                    $"vfs_cache_mode must be one of {{{string.Join(", ", AllowedVfsCacheModes)}}} -- 'writes' is the floor (Invariant I6)."));
            }

            // Invariant I7: never offerable as false. HostFormModel has no property that could
            // carry anything else -- this line is the single point where the invariant becomes a
            // written value, and it ignores the model entirely on purpose.
            networkMode = true;

            if (!int.TryParse(model.IdleUnmountSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idle)
                || idle < 0)
            {
                errors.Add(new HostFormValidationError(
                    HostFormFieldId.IdleUnmountSeconds,
                    $"Idle unmount seconds must be a non-negative whole number (got '{model.IdleUnmountSeconds}')."));
            }
            else
            {
                idleUnmountSeconds = idle;
            }
        }

        string? tmuxSession = null;
        if (model.Tmux)
        {
            tmuxSession = model.TmuxSession.Trim();
            if (string.IsNullOrWhiteSpace(tmuxSession))
            {
                errors.Add(new HostFormValidationError(
                    HostFormFieldId.TmuxSession, "Tmux session name is required when tmux is enabled."));
            }
        }

        if (!int.TryParse(model.ProbeIntervalSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var probeInterval)
            || probeInterval < 0)
        {
            errors.Add(new HostFormValidationError(
                HostFormFieldId.ProbeIntervalSeconds,
                $"Probe interval must be a non-negative whole number (got '{model.ProbeIntervalSeconds}')."));
        }

        if (errors.Count > 0)
        {
            return HostFormBuildResult.Invalid(errors);
        }

        var host = new HostConfig
        {
            Key = key,
            DisplayName = displayName,
            Hostname = hostname,
            Port = port,
            User = user,
            IdentityFile = identityFile,
            Mount = new MountConfig
            {
                Mode = model.Mode,
                Drive = drive,
                RemotePath = remotePath,
                VfsCacheMode = vfsCacheMode,
                NetworkMode = networkMode,
                IdleUnmountSeconds = idleUnmountSeconds,
            },
            Session = new SessionConfig
            {
                Autostart = model.Autostart,
                Reconnect = model.Reconnect,
                Tmux = model.Tmux,
                TmuxSession = tmuxSession,
                TabColor = model.TabColor.Trim(),
                ColorScheme = model.ColorScheme.Trim(),
            },
            Probe = new ProbeConfig
            {
                IntervalSeconds = probeInterval,
                DeepProbe = model.DeepProbe,
            },
        };

        return HostFormBuildResult.Ok(host);
    }

    /// <summary>Builds <paramref name="model"/> and, if it parses, hands it to
    /// <see cref="IHostConfigWriter.SaveHostAsync"/>. Never throws for the ordinary failure modes
    /// -- parse errors and writer-reported validation/IO errors are both returned, not thrown.</summary>
    public async Task<HostEditorSaveResult> SaveAsync(HostFormModel model, CancellationToken cancellationToken = default)
    {
        var build = Build(model);
        if (!build.Succeeded)
        {
            return new HostEditorSaveResult
            {
                Succeeded = false,
                FieldErrors = build.Errors,
                GeneralError = string.Join(Environment.NewLine, build.Errors.Select(e => e.Message)),
            };
        }

        var result = await _writer.SaveHostAsync(build.Host!, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return HostEditorSaveResult.Ok();
        }

        if (result.ValidationErrors.Count > 0)
        {
            _logger?.LogInformation(
                "Save of host {HostKey} refused: {ErrorCount} validation error(s)",
                model.Key, result.ValidationErrors.Count);

            return new HostEditorSaveResult
            {
                Succeeded = false,
                FieldErrors = HostValidationFieldMapper.Map(result.ValidationErrors, build.Host!.Key),
                // Never trimmed to only the mapped subset -- see the property's own remarks.
                GeneralError = string.Join(Environment.NewLine, result.ValidationErrors.Select(e => e.Message)),
            };
        }

        _logger?.LogWarning("Save of host {HostKey} failed: {Error}", model.Key, result.Error);
        return new HostEditorSaveResult { Succeeded = false, GeneralError = result.Error ?? "Save failed." };
    }

    /// <summary>Deletes a host, unmounting it first if it is currently mounted. Confirmation is
    /// the caller's job (a WPF concern); this method assumes the user has already agreed.</summary>
    /// <remarks>
    /// <para>
    /// <b>The drain belongs HERE, and this is the seam where it was missed.</b>
    /// <see cref="IHostConfigWriter.DeleteHostAsync"/> deliberately does not drain -- it has no
    /// access to mount state, which lives in <see cref="IMountSupervisor"/> and not in
    /// <c>BosunConfig</c> -- and its contract says the caller must drain first. An earlier version
    /// of this method asserted the opposite, that draining was the writer's job. Both halves were
    /// individually reasonable and neither drained: deleting a mounted host removed it from
    /// <c>hosts.toml</c> and left the drive letter live with nothing describing it, which is the
    /// orphaned-mount case Invariant I2 exists to prevent. The confirmation dialog even promised
    /// the user "its drive will be unmounted first".
    /// </para>
    /// <para>
    /// The UI is allowed to call <see cref="IMountSupervisor"/> -- that is the only permitted route
    /// to a mount operation (Invariant I4), and the tray's own unmount action already uses it. What
    /// the UI may not do is touch <c>IRcloneClient</c> directly.
    /// </para>
    /// <para>
    /// The unmount is requested and awaited before the config write. If it fails the delete is
    /// abandoned rather than proceeding: leaving a configured-but-unmountable host is recoverable,
    /// whereas a live drive letter with no configuration is the state that wedges Explorer.
    /// </para>
    /// </remarks>
    public async Task<HostEditorDeleteResult> DeleteAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKey);

        if (_supervisor is not null)
        {
            var snapshot = _supervisor.GetSnapshot().FirstOrDefault(h =>
                string.Equals(h.HostKey, hostKey, StringComparison.Ordinal));

            if (snapshot is not null &&
                snapshot.State is MountState.Mounted or MountState.Mounting or MountState.Draining)
            {
                try
                {
                    await _supervisor.RequestUnmountAsync(hostKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "Could not unmount {HostKey} before deleting it; delete abandoned", hostKey);
                    return new HostEditorDeleteResult
                    {
                        Succeeded = false,
                        Error = $"'{hostKey}' could not be unmounted, so it was not deleted: {ex.Message}",
                    };
                }
            }
        }

        var result = await _writer.DeleteHostAsync(hostKey, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return HostEditorDeleteResult.Ok();
        }

        var error = result.Error
            ?? (result.ValidationErrors.Count > 0
                ? string.Join(Environment.NewLine, result.ValidationErrors.Select(e => e.Message))
                : "Delete failed.");

        _logger?.LogWarning("Delete of host {HostKey} failed: {Error}", hostKey, error);
        return new HostEditorDeleteResult { Succeeded = false, Error = error };
    }
}
