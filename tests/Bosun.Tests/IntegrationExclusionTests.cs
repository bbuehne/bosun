using Xunit;

namespace Bosun.Tests;

/// <summary>
/// A canary proving the integration-test exclusion actually works (bs-ef4). This test is itself
/// marked Integration, so it must NOT run in the default suite or in CI.
/// </summary>
/// <remarks>
/// If it ever executes under <c>dotnet test --filter "Category!=Integration"</c>, the convention
/// has silently broken, and every safety claim resting on it — that no default-suite test can
/// reach a real rclone, WinFsp, or a drive letter — is void. It fails loudly rather than passing
/// quietly, so a broken filter is impossible to miss instead of being discovered the hard way.
/// </remarks>
public sealed class IntegrationExclusionTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void This_test_must_never_run_in_the_default_suite()
    {
        Assert.Fail(
            "Integration-category exclusion is broken: this test ran under the default filter. " +
            "CI and the default suite must use --filter \"Category!=Integration\". " +
            "See tests/Bosun.Tests/TestCategories.cs and bs-ef4.");
    }
}
