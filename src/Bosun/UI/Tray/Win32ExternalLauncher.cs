using Microsoft.Extensions.Logging;
using Diag = System.Diagnostics;

namespace Bosun.UI.Tray;

/// <summary>
/// Real <see cref="IExternalLauncher"/>, wrapping <see cref="Diag.Process"/> (aliased -- see
/// <c>Win32RcloneProcessLauncher</c>'s remarks for why: this project has its own
/// <c>Bosun.Rclone.Process</c> namespace elsewhere, and the pattern is kept consistent). Never
/// exercised by the default test suite (CLAUDE.md worktree-safety rules) -- launching a real
/// <c>wt.exe</c> or <c>explorer.exe</c> from an automated run has the same "wedges the
/// maintainer's desktop while he is working" problem as anything else in this codebase that spawns
/// a real, visible Win32 process, so this is left unverified by an integration test in this
/// delivery -- flagged as discovered follow-up work rather than silently skipped.
/// </summary>
public sealed class Win32ExternalLauncher : IExternalLauncher
{
    private readonly ILogger<Win32ExternalLauncher> _logger;

    public Win32ExternalLauncher(ILogger<Win32ExternalLauncher> logger)
    {
        _logger = logger;
    }

    public void OpenTerminal(string hostKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostKey);
        Start("wt.exe", ["-w", "0", "new-tab", "-p", hostKey], useShellExecute: true);
    }

    public void OpenInExplorer(string drive)
    {
        ArgumentException.ThrowIfNullOrEmpty(drive);
        Start("explorer.exe", [drive], useShellExecute: true);
    }

    private void Start(string fileName, IReadOnlyList<string> arguments, bool useShellExecute)
    {
        var startInfo = new Diag.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = useShellExecute,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Diag.Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Best-effort by design: a failure to open a terminal or Explorer window is an
            // inconvenience the user can retry, never a reason to crash Bosun or touch any mount
            // state. Never reaches IMountSupervisor or IRcloneClient either way.
            _logger.LogWarning(ex, "Failed to launch {FileName} {Arguments}", fileName, string.Join(' ', arguments));
        }
    }
}
