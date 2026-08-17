using Bosun.UI;

namespace Bosun.Tests.UI.Fakes;

internal sealed class FakeWindowPlacementStore : IWindowPlacementStore
{
    private WindowPlacement? _stored;

    public int SaveCallCount { get; private set; }
    public WindowPlacement? LastSaved { get; private set; }

    public FakeWindowPlacementStore(WindowPlacement? initial = null)
    {
        _stored = initial;
    }

    public WindowPlacement? TryLoad() => _stored;

    public void Save(WindowPlacement placement)
    {
        _stored = placement;
        LastSaved = placement;
        SaveCallCount++;
    }
}
