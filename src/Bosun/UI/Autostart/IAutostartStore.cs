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
}
