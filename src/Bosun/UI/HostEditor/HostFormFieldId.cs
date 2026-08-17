namespace Bosun.UI.HostEditor;

/// <summary>
/// Identifies one field on the host-editor form (bs-ww9.8, ADR-019), so a validation failure --
/// whether a client-side parse error or a <see cref="Bosun.Configuration.ConfigValidationError"/>
/// mapped back from a save attempt -- can be shown against the specific control that caused it,
/// rather than as an undifferentiated "save failed" (the brief's explicit requirement).
/// </summary>
public enum HostFormFieldId
{
    Key,
    DisplayName,
    Hostname,
    Port,
    User,
    IdentityFile,

    Drive,
    RemotePath,
    VfsCacheMode,

    /// <summary>No control on the form ever writes this -- Invariant I7 forbids offering
    /// <c>network_mode = false</c> at all -- but a mapped <see cref="Bosun.Configuration.ConfigValidationError"/>
    /// can still name it, so it exists as a target even though nothing in <c>HostEditorWindow</c>
    /// binds to it.</summary>
    NetworkMode,

    IdleUnmountSeconds,

    TmuxSession,

    ProbeIntervalSeconds,

    /// <summary>Not attributable to one specific control -- a config-wide error (e.g. a
    /// <c>global.*</c> rule) or one this mapper's heuristic did not recognise. Never used to hide
    /// an error: anything landing here is still shown, just without a field-level home. See
    /// <see cref="HostEditorSaveResult.GeneralError"/>, which always carries the complete,
    /// unfiltered list regardless of how field mapping went.</summary>
    General,
}
