namespace Bosun.UI;

/// <summary>
/// Persisted window geometry (ADR-018 rule 1: "remembers its size and position across runs").
/// <see cref="Left"/>/<see cref="Top"/>/<see cref="Width"/>/<see cref="Height"/> are always the
/// RESTORED (non-maximized) bounds, even when <see cref="IsMaximized"/> is true -- mirroring
/// <see cref="System.Windows.Window.RestoreBounds"/> -- so restoring to a maximized window still
/// has somewhere sane to go if the user later un-maximizes it.
/// </summary>
public sealed record WindowPlacement
{
    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required bool IsMaximized { get; init; }
}
