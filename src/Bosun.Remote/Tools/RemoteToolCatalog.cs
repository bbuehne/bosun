namespace Bosun.Remote.Tools;

/// <summary>
/// The one list of remote tools (ADR-024 "modular" addendum). Adding a tool means a new class
/// implementing <see cref="IRemoteTool"/>, one line in <see cref="Create"/>, and its tests.
/// Compile-time and explicit on purpose: no reflection scanning, no tools loaded from outside Bosun.
/// </summary>
public static class RemoteToolCatalog
{
    /// <summary>
    /// Builds the registry for a development area. Round 1 tools (bs-4xw.11) are added here; until
    /// then the list is empty.
    /// </summary>
    public static ToolRegistry Create(DevelopmentAreaGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        IRemoteTool[] tools =
        [
            // Round 1 tools register here (bs-4xw.11), one line each.
        ];

        return new ToolRegistry(tools);
    }
}
