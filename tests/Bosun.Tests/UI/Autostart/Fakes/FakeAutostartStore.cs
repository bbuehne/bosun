using Bosun.UI.Autostart;

namespace Bosun.Tests.UI.Autostart.Fakes;

/// <summary>In-memory <see cref="IAutostartStore"/> for exercising
/// <see cref="Bosun.UI.Autostart.AutostartRegistration"/>'s logic without ever touching the real
/// Run key (CLAUDE.md worktree-safety rules; bs-ojc.1's hard constraint).</summary>
public sealed class FakeAutostartStore : IAutostartStore
{
    private string? _value;

    /// <summary>Seeds the store as if a prior registration already existed -- used to simulate a
    /// stale registration left by an old exe path.</summary>
    public string? Value
    {
        get => _value;
        set => _value = value;
    }

    public int GetValueCallCount { get; private set; }
    public int SetValueCallCount { get; private set; }
    public int DeleteValueCallCount { get; private set; }

    /// <summary>When set, <see cref="GetValue"/>/<see cref="SetValue"/>/<see cref="DeleteValue"/>
    /// throw this instead of touching <see cref="_value"/> -- simulates a Registry access failure
    /// (permissions, corruption, whatever) so AutostartRegistration's fail-soft handling can be
    /// pinned.</summary>
    public Exception? ThrowOnAccess { get; set; }

    /// <summary>Seeds the StartupApproved override (bs-ojc.2) as if Task Manager had written it.
    /// <see langword="null"/> = no override.</summary>
    public byte[]? StartupApprovedData { get; set; }

    public int GetStartupApprovedCallCount { get; private set; }
    public int ClearStartupApprovedCallCount { get; private set; }

    /// <summary>When set, only <see cref="GetStartupApprovedData"/>/<see cref="ClearStartupApproved"/>
    /// throw this -- the Run-value calls keep working. Lets a test simulate the override key being
    /// unreadable/unwritable while the Run value itself is fine. (<see cref="ThrowOnAccess"/>
    /// also covers these calls.)</summary>
    public Exception? ThrowOnStartupApprovedAccess { get; set; }

    /// <summary>A realistic 12-byte StartupApproved value: 4-byte flag DWORD whose first byte is
    /// <paramref name="firstByte"/>, then an 8-byte FILETIME (arbitrary here).</summary>
    public static byte[] OverrideBytes(byte firstByte) =>
        [firstByte, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01];

    public byte[]? GetStartupApprovedData()
    {
        GetStartupApprovedCallCount++;
        ThrowIfInjected();
        return StartupApprovedData;
    }

    public void ClearStartupApproved()
    {
        ClearStartupApprovedCallCount++;
        ThrowIfInjected();
        StartupApprovedData = null;
    }

    private void ThrowIfInjected()
    {
        if (ThrowOnAccess is not null)
        {
            throw ThrowOnAccess;
        }

        if (ThrowOnStartupApprovedAccess is not null)
        {
            throw ThrowOnStartupApprovedAccess;
        }
    }

    public string? GetValue()
    {
        GetValueCallCount++;
        if (ThrowOnAccess is not null)
        {
            throw ThrowOnAccess;
        }

        return _value;
    }

    public void SetValue(string value)
    {
        SetValueCallCount++;
        if (ThrowOnAccess is not null)
        {
            throw ThrowOnAccess;
        }

        _value = value;
    }

    public void DeleteValue()
    {
        DeleteValueCallCount++;
        if (ThrowOnAccess is not null)
        {
            throw ThrowOnAccess;
        }

        _value = null;
    }
}
