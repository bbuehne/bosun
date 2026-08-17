namespace Bosun.UI.HostEditor;

/// <summary>One field-attributed validation failure on the host-editor form -- either a
/// client-side parse error from <see cref="HostEditorController.Build"/>, or a
/// <see cref="Configuration.ConfigValidationError"/> mapped back onto a field by
/// <see cref="HostValidationFieldMapper"/>.</summary>
public sealed record HostFormValidationError(HostFormFieldId Field, string Message);
