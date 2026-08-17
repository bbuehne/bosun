using Bosun.UI.Autostart;

namespace Bosun.Tests.UI.Autostart;

/// <summary>
/// Exercises <see cref="RegistryAutostartStore"/> against the REAL per-user Run key -- excluded
/// from the default suite (CLAUDE.md worktree-safety rules apply here exactly as they do to a real
/// drive letter or the real Terminal fragment path: no default-suite test may touch real,
/// persistent OS state). Uses a dedicated, private value name distinct from
/// <see cref="RegistryAutostartStore.DefaultValueName"/> so this can never collide with, or
/// disturb, a real Bosun install's own autostart registration on the machine it runs on. Cleans up
/// after itself unconditionally (try/finally) so a failed assertion never leaves the test value
/// behind. Run deliberately with
/// <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RegistryAutostartStoreIntegrationTests
{
    private const string TestValueName = "BosunAutostartIntegrationTest";

    [Fact]
    public void SetValue_then_GetValue_round_trips_through_the_real_Run_key()
    {
        var store = new RegistryAutostartStore(TestValueName);
        try
        {
            store.SetValue(@"""C:\test\Bosun.exe"" --autostart");

            Assert.Equal(@"""C:\test\Bosun.exe"" --autostart", store.GetValue());
        }
        finally
        {
            store.DeleteValue();
        }
    }

    [Fact]
    public void GetValue_returns_null_when_nothing_is_registered()
    {
        var store = new RegistryAutostartStore(TestValueName);
        store.DeleteValue(); // ensure a clean slate regardless of prior test ordering

        Assert.Null(store.GetValue());
    }

    [Fact]
    public void DeleteValue_removes_a_registration_and_is_a_no_op_if_already_absent()
    {
        var store = new RegistryAutostartStore(TestValueName);
        store.SetValue(@"""C:\test\Bosun.exe"" --autostart");

        store.DeleteValue();
        Assert.Null(store.GetValue());

        // Calling again with nothing present must not throw.
        var exception = Record.Exception(store.DeleteValue);
        Assert.Null(exception);
    }

    [Fact]
    public void SetValue_overwrites_an_existing_registration()
    {
        var store = new RegistryAutostartStore(TestValueName);
        try
        {
            store.SetValue(@"""C:\old\Bosun.exe"" --autostart");
            store.SetValue(@"""C:\new\Bosun.exe"" --autostart");

            Assert.Equal(@"""C:\new\Bosun.exe"" --autostart", store.GetValue());
        }
        finally
        {
            store.DeleteValue();
        }
    }
}
