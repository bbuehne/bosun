namespace Bosun.UI.HostEditor;

/// <summary>
/// Prompts the user to pick an existing file for <c>identity_file</c> (Invariant I10: key-based
/// auth only, and <see cref="Configuration.ConfigValidator"/> requires the path to exist). Behind
/// an interface, same reasoning as every other real-OS-surface seam in this codebase
/// (<see cref="IAppWindow"/>, <see cref="Tray.IExternalLauncher"/>): a file dialog is real Win32 UI
/// and has no place in the default test suite.
/// </summary>
public interface IIdentityFilePicker
{
    /// <summary>Shows a file-open dialog restricted to existing files. Returns the chosen path, or
    /// <see langword="null"/> if the user cancelled.</summary>
    /// <param name="initialDirectory">Directory to open the dialog in, if it exists; otherwise the
    /// dialog falls back to its own default.</param>
    string? PickIdentityFile(string? initialDirectory);
}
