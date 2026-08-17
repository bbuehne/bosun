using System.IO;
using Microsoft.Win32;

namespace Bosun.UI.HostEditor;

/// <summary>
/// Real <see cref="IIdentityFilePicker"/>, wrapping <see cref="OpenFileDialog"/>. Never exercised
/// by the default test suite -- a real file dialog is exactly the kind of live Win32 UI
/// CLAUDE.md's worktree-safety rules keep out of automated runs.
/// </summary>
public sealed class Win32IdentityFilePicker : IIdentityFilePicker
{
    public string? PickIdentityFile(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select identity file",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "All files (*.*)|*.*",
        };

        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
