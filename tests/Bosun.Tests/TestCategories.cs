namespace Bosun.Tests;

/// <summary>
/// Trait names and values used to keep tests that touch real systems out of the default suite.
/// </summary>
/// <remarks>
/// <para>
/// Mark such a test with xUnit's built-in trait, using these constants so the string is never
/// mistyped (a mistyped category silently rejoins the default suite, which is the failure mode
/// this whole convention exists to prevent):
/// </para>
/// <code>
/// [Fact]
/// [Trait(TestCategories.Category, TestCategories.Integration)]
/// public void Mounts_a_real_drive() { ... }
/// </code>
/// <para>
/// Apply it to anything that touches a live <c>rclone rcd</c>, WinFsp, a drive letter, a real
/// SFTP host, the real Windows Terminal fragment path, or the real Win32 APIs behind
/// <c>ISessionMonitor</c>.
/// </para>
/// <code>
/// dotnet test                                                    // safe; excludes Integration
/// dotnet test --settings tests/Bosun.Tests/integration.runsettings  // deliberate opt-in
/// </code>
/// <para>
/// The exclusion is the DEFAULT, applied via <c>bosun.runsettings</c> and the project's
/// <c>RunSettingsFilePath</c>, so a bare <c>dotnet test</c> — the command everyone types without
/// thinking — cannot reach a real system. Note that a command-line <c>--filter</c> is ANDed with
/// that default rather than replacing it, so asking for <c>Category=Integration</c> that way
/// matches nothing and looks like a clean run; use the settings file instead.
/// </para>
/// <para>
/// The stake is concrete rather than stylistic. Bosun mounts real drive letters via WinFsp, and
/// a drive letter pointing at an unreachable host blocks Explorer, <c>dir</c>, and file dialogs
/// process-wide across the OS (Invariant I2, ADR-005). A default-suite test reaching a real
/// <c>IRcloneClient</c> could wedge the machine it runs on — a GitHub runner, or the maintainer's
/// desktop while he is working. CI runs the exclusion filter so that cannot happen there.
/// </para>
/// <para>
/// The attribute is not a substitute for care. An integration test must still clean up after
/// itself and must never assume a particular host is reachable.
/// </para>
/// </remarks>
public static class TestCategories
{
    /// <summary>The trait name. Matches the <c>Category</c> used by <c>dotnet test --filter</c>.</summary>
    public const string Category = "Category";

    /// <summary>Touches a real external system. Excluded from the default suite and from CI.</summary>
    public const string Integration = "Integration";
}
