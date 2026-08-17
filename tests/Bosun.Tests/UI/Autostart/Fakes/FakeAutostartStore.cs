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
