using Microsoft.Win32;

namespace Bosun.UI.Autostart;

/// <summary>
/// Real <see cref="IAutostartStore"/>: a single <c>REG_SZ</c> value under the per-user Run key,
/// <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the Run key and not a <c>shell:startup</c> shortcut file.</b> bs-ojc.1's brief named three
/// candidates: a <c>.lnk</c> via COM (<c>IShellLinkW</c>/<c>IPersistFile</c>), a <c>.url</c> file,
/// or a Registry Run entry. The <c>.url</c> (Internet Shortcut) format has no documented,
/// reliable field for command-line arguments -- double-clicking one that targets a local
/// <c>.exe</c> launches it bare, which cannot carry
/// <see cref="Bosun.UI.LaunchContextDetector.AutostartArgument"/> and is disqualified outright:
/// without that flag Bosun opens its window at every login, which is the exact regression
/// ADR-018/ADR-012 exist to prevent.
/// </para>
/// <para>
/// That leaves <c>.lnk</c> vs. Registry. A <c>.lnk</c> requires either the
/// <c>IWshRuntimeLibrary</c> COM automation object or hand-declared <c>IShellLinkW</c> /
/// <c>IPersistFile</c> COM interop -- a second Win32/COM interop surface in the project.
/// CLAUDE.md and this project's own architecture principle are explicit that Win32 interop stays
/// behind <c>ISessionMonitor</c>'s single P/Invoke file (see
/// <c>Bosun.SessionMonitor.Interop.NativeTcpTable</c>'s remarks: "if a second DllImport shows up
/// anywhere else, that's an architecture violation, not a style nit") and that a second interop
/// surface is a stop-and-report situation, not a call to make silently. The Registry Run key needs
/// none of that: <see cref="Registry"/> is plain managed BCL surface, not P/Invoke or COM, and it
/// satisfies every functional requirement a <c>.lnk</c> would -- Windows 10/11's Task Manager
/// "Startup apps" tab lists Run-key entries and <c>shell:startup</c> shortcuts identically, so
/// there is no user-facing discoverability loss. Per-user (<c>HKEY_CURRENT_USER</c>), never
/// <c>HKEY_LOCAL_MACHINE</c>, for the same reason ADR-004 keeps the supervisor out of a
/// SYSTEM-scoped Windows Service: this must run in, and only in, the interactive logon session
/// that owns the drive letters.
/// </para>
/// <para>
/// This is a deliberate deviation from the bd issue's literal "shortcut in shell:startup" wording,
/// recorded here (and in the implementer's report) rather than made silently, exactly as
/// CLAUDE.md's "when a decision looks wrong, say so" principle asks. The observable behaviour bd
/// bs-ojc.1 actually specifies -- launch at login, pass <c>--autostart</c>, toggleable,
/// idempotent, self-healing against a stale path, checked-state derived from reality -- is fully
/// met either way; only the OS mechanism underneath differs.
/// </para>
/// <para>
/// Never exercised by the default test suite: it is the one thing in this feature that reaches a
/// real, persistent piece of OS state, matching every other "real implementation of an injectable
/// seam" in this codebase (<c>Win32ExternalLauncher</c>, <c>Win32TcpConnectionReader</c>). A
/// dedicated, cleanly-named value name keeps even the Integration test from ever touching the real
/// <c>"Bosun"</c> entry that a real install would use.
/// </para>
/// </remarks>
public sealed class RegistryAutostartStore : IAutostartStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The default value name a real Bosun install registers under.</summary>
    public const string DefaultValueName = "Bosun";

    private readonly string _valueName;

    public RegistryAutostartStore(string valueName = DefaultValueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(valueName);
        _valueName = valueName;
    }

    public string? GetValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    public void SetValue(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(_valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
