using Bosun.Supervisor;
using Bosun.Status;

namespace Bosun.UI.Tray;

public enum HostMenuActionKind
{
    Mount,
    Unmount,
    OpenTerminal,
    OpenInExplorer,
}

/// <summary>One action offered for one host, in the tray context menu and the window's per-row
/// menu alike -- both are built from the same <see cref="HostContextMenuBuilder"/> call.</summary>
public sealed record HostMenuAction
{
    public required HostMenuActionKind Kind { get; init; }
    public required string Label { get; init; }
    public required bool IsEnabled { get; init; }
}

/// <summary>
/// Pure derivation of "what actions can I offer for this host, and are they currently
/// actionable" from a <see cref="HostStatusRow"/>. Kept separate from any WPF menu type so it is
/// unit-testable -- the bs-ww9.5 brief's menu requirements (mount/unmount, open terminal, open in
/// Explorer, and ADR-015's "offer a way to un-park") are exactly what this class decides.
/// </summary>
public static class HostContextMenuBuilder
{
    /// <summary>
    /// Builds the four standard actions for <paramref name="row"/>, in a fixed order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mount doubles as "un-park" (ADR-015).</b> A parked host is not in any special
    /// <see cref="MountState"/> -- it keeps cycling <c>Ready</c>/<c>Unreachable</c> like any other
    /// idle host, with <see cref="HostStatusRow.UserParked"/> carrying the fact that it will not
    /// auto-mount. The SAME <c>RequestMountAsync</c> call that mounts an un-parked host also clears
    /// the park (per ADR-015's "explicit user remount"), so no separate action kind is needed --
    /// only a label that tells the user what clicking it will do.
    /// </para>
    /// <para>
    /// <b>Enablement mirrors <c>IMountSupervisor</c>'s own accepted states</b> (its doc comments):
    /// Mount is a no-op unless <see cref="MountState.Ready"/> or <see cref="MountState.Unreachable"/>;
    /// Unmount is a no-op unless <see cref="MountState.Mounting"/> or <see cref="MountState.Mounted"/>.
    /// Open Terminal has no mount-state precondition -- launching a terminal profile does not touch
    /// the mount supervisor at all (Invariant I4 scopes that boundary to mount/unmount only), so it
    /// is always enabled. Open in Explorer needs an actual, currently-mounted drive letter to point
    /// at, so it is enabled only when the host is <see cref="MountState.Mounted"/> and
    /// <see cref="HostStatusRow.Drive"/> is populated.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<HostMenuAction> Build(HostStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var canMount = row.State is MountState.Ready or MountState.Unreachable;
        var canUnmount = row.State is MountState.Mounting or MountState.Mounted;
        var canOpenInExplorer = row.State == MountState.Mounted && row.Drive is not null;

        return
        [
            new HostMenuAction
            {
                Kind = HostMenuActionKind.Mount,
                Label = row.UserParked ? "Mount (un-park)" : "Mount",
                IsEnabled = canMount,
            },
            new HostMenuAction
            {
                Kind = HostMenuActionKind.Unmount,
                Label = "Unmount",
                IsEnabled = canUnmount,
            },
            new HostMenuAction
            {
                Kind = HostMenuActionKind.OpenTerminal,
                Label = "Open terminal",
                IsEnabled = true,
            },
            new HostMenuAction
            {
                Kind = HostMenuActionKind.OpenInExplorer,
                Label = "Open in Explorer",
                IsEnabled = canOpenInExplorer,
            },
        ];
    }
}
