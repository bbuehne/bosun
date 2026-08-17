using Bosun.UI;

namespace Bosun.Tests.UI.Fakes;

internal sealed class FakeVirtualScreenProvider : IVirtualScreenProvider
{
    private readonly ScreenBounds _bounds;

    public FakeVirtualScreenProvider(ScreenBounds bounds)
    {
        _bounds = bounds;
    }

    public ScreenBounds GetVirtualScreenBounds() => _bounds;
}
