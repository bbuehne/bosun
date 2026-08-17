namespace Bosun.UI.HostEditor;

/// <summary>
/// Mutable, WPF-agnostic backing state for the host-editor form (bs-ww9.8, ADR-019). Every
/// field a control on <c>HostEditorWindow</c> binds to lives here as a plain, bindable property --
/// numeric fields are kept as <see cref="string"/> so a control can hold an invalid or empty
/// in-progress value without losing what the user typed; <see cref="HostEditorController.Build"/>
/// is what parses and validates them.
/// </summary>
/// <remarks>
/// Deliberately has no <c>NetworkMode</c> property. Invariant I7 forbids the form from ever
/// offering <c>network_mode = false</c> as a choice, and the simplest way to guarantee that is to
/// give the model nothing that could carry the value -- <see cref="HostEditorController.Build"/>
/// hardcodes <c>true</c> for every host whose mount mode is not <see cref="Configuration.MountMode.None"/>.
/// </remarks>
public sealed class HostFormModel
{
    /// <summary>The TOML key -- also the <c>~/.ssh/config</c> Host alias the generated Terminal
    /// profile runs (<c>ssh &lt;key&gt;</c>, ADR-013). Editable only when <see cref="IsNewHost"/>:
    /// changing an existing host's key is a rename, and <see cref="Configuration.IHostConfigWriter.SaveHostAsync"/>
    /// adds-or-replaces by key, so saving under a different key would leave the old entry behind
    /// rather than renaming it. <c>HostEditorWindow</c> keeps the control read-only for an edit.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>True when this form is creating a new host (key editable, defaults from
    /// <see cref="Configuration.NewHostDefaults.Create"/>); false when editing an existing one
    /// (key fixed).</summary>
    public bool IsNewHost { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;

    /// <summary>String, not <see langword="int"/> -- see the class remarks.</summary>
    public string Port { get; set; } = "22";

    public string User { get; set; } = string.Empty;

    /// <summary>Literal, unexpanded path, exactly as it will be written to TOML. Invariant I10
    /// requires this to exist on disk; <c>HostEditorWindow</c> offers a file picker, and existence
    /// is re-checked authoritatively by <see cref="Configuration.ConfigValidator"/> on save.</summary>
    public string IdentityFile { get; set; } = string.Empty;

    public Configuration.MountMode Mode { get; set; } = Configuration.MountMode.OnDemand;

    /// <summary>Only meaningful when <see cref="Mode"/> is not <see cref="Configuration.MountMode.None"/>
    /// -- see <see cref="HostEditorController.IsMountDetailEnabled"/>.</summary>
    public string Drive { get; set; } = string.Empty;

    public string RemotePath { get; set; } = string.Empty;

    /// <summary>Restricted to <see cref="HostEditorController.AllowedVfsCacheModes"/> by the
    /// control that binds this (a closed combo box, never free text) -- Invariant I6 makes
    /// anything weaker than <c>"writes"</c> a correctness bug, not a preference, so the form must
    /// never let the value drift outside that set.</summary>
    public string VfsCacheMode { get; set; } = Configuration.MountConfig.DefaultVfsCacheMode;

    public string IdleUnmountSeconds { get; set; } = "0";

    public bool Autostart { get; set; }
    public bool Reconnect { get; set; } = true;
    public bool Tmux { get; set; }

    /// <summary>Only meaningful when <see cref="Tmux"/> is true -- see
    /// <see cref="HostEditorController.IsTmuxSessionEnabled"/>.</summary>
    public string TmuxSession { get; set; } = string.Empty;

    public string TabColor { get; set; } = string.Empty;
    public string ColorScheme { get; set; } = string.Empty;

    public string ProbeIntervalSeconds { get; set; } = "60";
    public bool DeepProbe { get; set; } = true;
}
