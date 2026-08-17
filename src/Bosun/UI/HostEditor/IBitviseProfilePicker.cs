namespace Bosun.UI.HostEditor;

/// <summary>
/// Prompts the user to pick a Bitvise/Tunnelier profile file to import (bs-ww9.9, ADR-019). Behind
/// an interface, same reasoning as every other real-OS-surface seam in this codebase
/// (<see cref="IIdentityFilePicker"/>, <see cref="IDriveLetterInspector"/>): a file dialog is real
/// Win32 UI and has no place in the default test suite.
/// </summary>
public interface IBitviseProfilePicker
{
    /// <summary>Shows a file-open dialog restricted to Bitvise/Tunnelier profile files
    /// (<c>*.tlp</c>, <c>*.bscp</c>). Returns the chosen path, or <see langword="null"/> if the
    /// user cancelled.</summary>
    string? PickProfileFile();
}
