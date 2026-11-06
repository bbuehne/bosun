namespace Bosun.UI.Autostart;

/// <summary>
/// The single low-level read/write/delete seam <see cref="AutostartRegistration"/> uses to touch
/// whatever OS mechanism actually records "launch Bosun at login". Kept as its own interface, one
/// level below <see cref="IAutostartRegistration"/>, so <see cref="AutostartRegistration"/>'s
/// idempotent/self-healing/reality-derived LOGIC (the part bs-ojc.1's acceptance criteria actually
/// cares about) is unit-tested against an in-memory fake, while <see cref="RegistryAutostartStore"/>
/// -- the only thing in this project that touches the real per-user Run key -- is exercised
/// separately, and only by a <c>Category=Integration</c> test.
/// </summary>
public interface IAutostartStore
{
    /// <summary>The current raw value, or <see langword="null"/> if nothing is registered.</summary>
    string? GetValue();

    /// <summary>Writes (creating or overwriting) the registration value.</summary>
    void SetValue(string value);

    /// <summary>Removes the registration value. A no-op if it was already absent.</summary>
    void DeleteValue();

    /// <summary>
    /// The raw bytes of Windows' per-user startup OVERRIDE for this registration (bs-ojc.2), or
    /// <see langword="null"/> if there is none. Task Manager's "Startup apps" tab and Settings >
    /// Apps > Startup record Enable/Disable under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c>, a
    /// separate <c>REG_BINARY</c> value with the same name as the Run value; the Run value itself
    /// is left intact. A value that is present but is not binary is returned as an EMPTY array
    /// (present, unusable) so the caller can tell it apart from absent. Interpreting the bytes is
    /// <see cref="AutostartRegistration"/>'s job, so that rule is unit-tested against a fake.
    /// </summary>
    byte[]? GetStartupApprovedData();

    /// <summary>Removes the startup override, if any, so Windows treats the registration as
    /// enabled again (absence means enabled). A no-op if it was already absent.</summary>
    void ClearStartupApproved();
}
