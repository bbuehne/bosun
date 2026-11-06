using Bosun.Tests.UI.Autostart.Fakes;
using Bosun.UI;
using Bosun.UI.Autostart;

namespace Bosun.Tests.UI.Autostart;

/// <summary>
/// bs-ojc.1 / E10a: exercises <see cref="AutostartRegistration"/>'s idempotent/self-healing/
/// reality-derived logic against an in-memory <see cref="FakeAutostartStore"/>. No test here
/// touches the real Run key -- that is <see cref="RegistryAutostartStore"/>'s job, covered
/// separately by a <c>Category=Integration</c> test.
/// </summary>
public sealed class AutostartRegistrationTests
{
    private const string ExePath = @"C:\Program Files\Bosun\Bosun.exe";

    private static AutostartRegistration CreateSut(FakeAutostartStore store, string? exePath = ExePath) =>
        new(store, processPathProvider: () => exePath);

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenNothingIsRegistered()
    {
        var store = new FakeAutostartStore();
        var sut = CreateSut(store);

        Assert.False(sut.IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenARegistrationExists()
    {
        var store = new FakeAutostartStore { Value = $"\"{ExePath}\" --autostart" };
        var sut = CreateSut(store);

        Assert.True(sut.IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReadsTheStoreEveryCall_NeverCaches()
    {
        // This is the acceptance criterion in bs-ojc.1's own words: "read whether the shortcut
        // currently exists, rather than caching a belief about it." Toggling the underlying store
        // out from under the SUT (simulating the user deleting the registration by hand, or a
        // second process) must be reflected on the very next call.
        var store = new FakeAutostartStore();
        var sut = CreateSut(store);

        Assert.False(sut.IsEnabled());

        store.Value = $"\"{ExePath}\" --autostart";
        Assert.True(sut.IsEnabled());

        store.Value = null;
        Assert.False(sut.IsEnabled());
    }

    [Fact]
    public void Enable_WritesTheCurrentExePath_AndTheAutostartArgument()
    {
        var store = new FakeAutostartStore();
        var sut = CreateSut(store);

        var result = sut.Enable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.NotNull(store.Value);
        Assert.Contains(ExePath, store.Value);
        Assert.Contains(LaunchContextDetector.AutostartArgument, store.Value);
    }

    [Fact]
    public void Enable_ReferencesLaunchContextDetectors_AutostartArgument_NotAHardcodedString()
    {
        // Pins the exact contract ADR-018 rule 2 depends on: whatever LaunchContextDetector
        // currently defines as its flag is exactly what gets written, so the two can never drift
        // independently.
        var store = new FakeAutostartStore();
        var sut = CreateSut(store);

        sut.Enable();

        Assert.EndsWith(LaunchContextDetector.AutostartArgument, store.Value);
    }

    [Fact]
    public void Enable_IsANoOp_WhenAlreadyEnabledWithTheSameCommandLine()
    {
        var store = new FakeAutostartStore();
        var sut = CreateSut(store);

        sut.Enable();
        var firstValue = store.Value;
        var result = sut.Enable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.Equal(firstValue, store.Value);
    }

    [Fact]
    public void Enable_RewritesAStaleRegistration_PointingAtAnOldExePath()
    {
        // The exact scenario bs-ojc.1 calls "guaranteed to happen": the exe was moved, or a new
        // release was unpacked somewhere else. A stale value already sits in the store before
        // Enable() is ever called on this SUT.
        var store = new FakeAutostartStore { Value = @"""C:\old\location\Bosun.exe"" --autostart" };
        var sut = CreateSut(store, exePath: @"C:\new\location\Bosun.exe");

        var result = sut.Enable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.Contains(@"C:\new\location\Bosun.exe", store.Value);
        Assert.DoesNotContain(@"C:\old\location\Bosun.exe", store.Value);
        Assert.Contains(LaunchContextDetector.AutostartArgument, store.Value);
    }

    [Fact]
    public void Enable_Fails_WithoutThrowing_WhenNoProcessPathIsAvailable()
    {
        var store = new FakeAutostartStore();
        var sut = CreateSut(store, exePath: null);

        var result = sut.Enable();

        Assert.Equal(AutostartResult.Failed, result);
        Assert.Null(store.Value);
    }

    [Fact]
    public void Enable_Fails_WithoutThrowing_WhenTheStoreThrows()
    {
        var store = new FakeAutostartStore { ThrowOnAccess = new InvalidOperationException("access denied") };
        var sut = CreateSut(store);

        var exception = Record.Exception(() => sut.Enable());
        var result = sut.Enable();

        Assert.Null(exception);
        Assert.Equal(AutostartResult.Failed, result);
    }

    [Fact]
    public void Disable_RemovesAnExistingRegistration()
    {
        var store = new FakeAutostartStore { Value = $"\"{ExePath}\" --autostart" };
        var sut = CreateSut(store);

        var result = sut.Disable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.Null(store.Value);
        Assert.False(sut.IsEnabled());
    }

    [Fact]
    public void Disable_IsANoOp_WhenNothingIsRegistered()
    {
        var store = new FakeAutostartStore();
        var sut = CreateSut(store);

        var result = sut.Disable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.Null(store.Value);
    }

    [Fact]
    public void Disable_Fails_WithoutThrowing_WhenTheStoreThrows()
    {
        var store = new FakeAutostartStore { ThrowOnAccess = new InvalidOperationException("access denied") };
        var sut = CreateSut(store);

        var exception = Record.Exception(() => sut.Disable());
        var result = sut.Disable();

        Assert.Null(exception);
        Assert.Equal(AutostartResult.Failed, result);
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WithoutThrowing_WhenTheStoreThrows()
    {
        var store = new FakeAutostartStore { ThrowOnAccess = new InvalidOperationException("access denied") };
        var sut = CreateSut(store);

        var exception = Record.Exception(() => sut.IsEnabled());

        Assert.Null(exception);
        Assert.False(sut.IsEnabled());
    }

    // ---- bs-ojc.2: Task Manager's StartupApproved\Run override ----

    private const string RunValue = "\"C:\\Program Files\\Bosun\\Bosun.exe\" --autostart";

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenRunValueExists_AndThereIsNoOverride()
    {
        var store = new FakeAutostartStore { Value = RunValue, StartupApprovedData = null };

        Assert.True(CreateSut(store).IsEnabled());
    }

    [Theory]
    [InlineData((byte)0x03)] // what Task Manager writes when the user disables an entry
    [InlineData((byte)0x01)]
    [InlineData((byte)0x07)]
    public void IsEnabled_ReturnsFalse_WhenRunValueExists_ButOverrideMarksItDisabled(byte firstByte)
    {
        var store = new FakeAutostartStore
        {
            Value = RunValue,
            StartupApprovedData = FakeAutostartStore.OverrideBytes(firstByte),
        };

        Assert.False(CreateSut(store).IsEnabled());
    }

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x06)] // written when the user re-enables an entry in Settings
    public void IsEnabled_ReturnsTrue_WhenRunValueExists_AndOverrideMarksItEnabled(byte firstByte)
    {
        var store = new FakeAutostartStore
        {
            Value = RunValue,
            StartupApprovedData = FakeAutostartStore.OverrideBytes(firstByte),
        };

        Assert.True(CreateSut(store).IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenThereIsNoRunValue_EvenIfAnEnabledOverrideRemains()
    {
        var store = new FakeAutostartStore
        {
            Value = null,
            StartupApprovedData = FakeAutostartStore.OverrideBytes(0x02),
        };

        Assert.False(CreateSut(store).IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenThereIsNoRunValue_AndOverrideIsDisabled()
    {
        var store = new FakeAutostartStore
        {
            Value = null,
            StartupApprovedData = FakeAutostartStore.OverrideBytes(0x03),
        };

        Assert.False(CreateSut(store).IsEnabled());
    }

    [Fact]
    public void IsEnabled_TreatsAnEmptyOverrideAsNoUsableOverride_AndStaysEnabled()
    {
        // Chosen rule for malformed data: no first byte (or a non-binary value, which the real
        // store reports as an empty array) is not a user decision we can honour; Windows' default
        // with no usable override is enabled.
        var store = new FakeAutostartStore { Value = RunValue, StartupApprovedData = [] };

        Assert.True(CreateSut(store).IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReadsFirstByteOnly_ForAShortButNonEmptyOverride()
    {
        var disabled = new FakeAutostartStore { Value = RunValue, StartupApprovedData = [0x03] };
        var enabled = new FakeAutostartStore { Value = RunValue, StartupApprovedData = [0x02] };

        Assert.False(CreateSut(disabled).IsEnabled());
        Assert.True(CreateSut(enabled).IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WithoutThrowing_WhenReadingTheOverrideThrows()
    {
        var store = new FakeAutostartStore
        {
            Value = RunValue,
            ThrowOnStartupApprovedAccess = new InvalidOperationException("access denied"),
        };
        var sut = CreateSut(store);

        var exception = Record.Exception(() => sut.IsEnabled());

        Assert.Null(exception);
        Assert.False(sut.IsEnabled());
    }

    [Fact]
    public void IsEnabled_DoesNotConsultTheOverride_WhenThereIsNoRunValue()
    {
        var store = new FakeAutostartStore { Value = null };

        CreateSut(store).IsEnabled();

        Assert.Equal(0, store.GetStartupApprovedCallCount);
    }

    [Fact]
    public void Enable_ClearsATaskManagerDisabledOverride_SoStartupIsGenuinelyOn()
    {
        var store = new FakeAutostartStore
        {
            Value = RunValue,
            StartupApprovedData = FakeAutostartStore.OverrideBytes(0x03),
        };
        var sut = CreateSut(store);
        Assert.False(sut.IsEnabled());

        var result = sut.Enable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.Null(store.StartupApprovedData);
        Assert.True(sut.IsEnabled());
    }

    [Fact]
    public void Enable_ReturnsFailed_WhenClearingTheOverrideFails_NeverSuccessWhileDisabled()
    {
        var store = new FakeAutostartStore
        {
            StartupApprovedData = FakeAutostartStore.OverrideBytes(0x03),
            ThrowOnStartupApprovedAccess = new UnauthorizedAccessException("denied"),
        };
        var sut = CreateSut(store);

        AutostartResult result = default;
        var exception = Record.Exception(() => result = sut.Enable());

        Assert.Null(exception);
        Assert.Equal(AutostartResult.Failed, result);
        Assert.False(sut.IsEnabled());
    }

    [Fact]
    public void Disable_RemovesTheRunValue_ButLeavesAnOverrideAlone()
    {
        var overrideBytes = FakeAutostartStore.OverrideBytes(0x03);
        var store = new FakeAutostartStore { Value = RunValue, StartupApprovedData = overrideBytes };

        var result = CreateSut(store).Disable();

        Assert.Equal(AutostartResult.Success, result);
        Assert.Null(store.Value);
        Assert.Equal(0, store.ClearStartupApprovedCallCount);
        Assert.Same(overrideBytes, store.StartupApprovedData);
    }

    [Fact]
    public void Constructor_Throws_WhenStoreIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new AutostartRegistration(null!));
    }
}
