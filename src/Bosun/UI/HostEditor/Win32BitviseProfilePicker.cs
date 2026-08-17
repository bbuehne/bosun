using Microsoft.Win32;

namespace Bosun.UI.HostEditor;

/// <summary>
/// Real <see cref="IBitviseProfilePicker"/>, wrapping <see cref="OpenFileDialog"/>. Never exercised
/// by the default test suite -- a real file dialog is exactly the kind of live Win32 UI
/// CLAUDE.md's worktree-safety rules keep out of automated runs.
/// </summary>
public sealed class Win32BitviseProfilePicker : IBitviseProfilePicker
{
    public string? PickProfileFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Bitvise/Tunnelier profile",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "Bitvise profiles (*.tlp;*.bscp)|*.tlp;*.bscp|All files (*.*)|*.*",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
