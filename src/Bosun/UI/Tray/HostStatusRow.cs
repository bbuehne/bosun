using Bosun.Supervisor;

namespace Bosun.UI.Tray;

/// <summary>
/// One row of the tray/window binding surface (bs-ww9.5/bs-ww9.6 seam -- see
/// <see cref="IStatusReadModel"/>'s remarks). Deliberately plain data: no WPF types, no commands,
/// no formatting beyond <see cref="StatusText"/> itself -- "return data; let the view render it"
/// (bs-ww9.6 brief).
/// </summary>
public sealed record HostStatusRow
{
    public required string HostKey { get; init; }
    public required string DisplayName { get; init; }
    public required MountState State { get; init; }

    /// <summary>The drive letter this host is actually mounted at right now, or
    /// <see langword="null"/> if it is not currently mounted. Not the CONFIGURED drive -- a host
    /// that is <c>Ready</c> is not occupying a drive letter yet, regardless of what
    /// <c>hosts.toml</c> says it will use once mounted.</summary>
    public string? Drive { get; init; }

    /// <summary>True if the user explicitly unmounted this host and it has not since been
    /// remounted (ADR-015). A parked host must be rendered as deliberate, never as a fault --
    /// see <see cref="HostContextMenuBuilder"/> for how the Mount action doubles as "un-park".</summary>
    public required bool IsParked { get; init; }

    /// <summary>
    /// Causal, human-readable status text -- ADR-012 Decision 3: "P: is not mounted -- WinFsp is
    /// not installed", never "some features unavailable". Producing GOOD text here (distinguishing
    /// every cause bs-ww9.6 enumerates: process-wide mounting unavailable, unreachable, reachable
    /// but repeatedly failing to mount, parked, mounted, on-demand-and-resting) is bs-ww9.6's job,
    /// not this epic's -- see the seam note on <see cref="IStatusReadModel"/>.
    /// </summary>
    public required string StatusText { get; init; }
}
