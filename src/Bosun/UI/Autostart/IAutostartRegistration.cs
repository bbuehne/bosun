namespace Bosun.UI.Autostart;

/// <summary>Outcome of an <see cref="IAutostartRegistration"/> enable/disable call. Never an
/// exception -- see the interface's remarks.</summary>
public enum AutostartResult
{
    Success,
    Failed,
}

/// <summary>
/// Registers (or unregisters) Bosun to launch at login, per bs-ojc.1 / E10a. The whole point of
/// this interface is that <see cref="Bosun.UI.Tray.TrayIconController"/>'s checkable menu item can
/// be built and tested against a fake, without any default-suite test writing to the real
/// autostart location -- the same rule CLAUDE.md already applies to the real drive letters, the
/// real rclone rcd, and the real Terminal fragment path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure is never fatal and never throws.</b> ADR-012's fail-soft principle applies here
/// exactly as it does to a missing WinFsp or a bad config: autostart is a convenience, not a
/// mount. Every implementation of this interface must catch its own exceptions and report failure
/// through <see cref="AutostartResult.Failed"/> (or, for <see cref="IsEnabled"/>, by returning
/// <see langword="false"/>) rather than letting anything escape into a
/// <c>DispatcherTimer.Tick</c> or a menu <c>Click</c> handler.
/// </para>
/// <para>
/// <b><see cref="IsEnabled"/> is always read from reality, never cached.</b> It must reflect
/// whatever is actually registered right now -- a toggle reading "on" after the user (or a
/// reinstall, or a manual edit) removed the registration by hand is worse than no toggle at all
/// (bs-ojc.1's acceptance criteria).
/// </para>
/// <para>
/// <b><see cref="Enable"/> is idempotent and self-healing.</b> Calling it when already enabled is
/// a no-op in effect (it rewrites the same value). Calling it when a STALE registration exists --
/// pointing at an old executable path because the exe was moved or a new release was unpacked
/// elsewhere -- corrects it, because the write is always unconditional: there is no separate
/// "is the existing registration stale" branch to get wrong. The target is always
/// <see cref="Environment.ProcessPath"/>, the *running* executable's own path, never a
/// hardcoded location -- Bosun ships as a single file the user can put anywhere.
/// </para>
/// <para>
/// <b>The launch argument is <see cref="Bosun.UI.LaunchContextDetector.AutostartArgument"/>,
/// referenced, never retyped.</b> This is the entire mechanism ADR-018 rule 2 depends on: without
/// it, Bosun would show its window at every login, which ADR-012 argues at length trains a user to
/// dismiss the app reflexively.
/// </para>
/// </remarks>
public interface IAutostartRegistration
{
    /// <summary>Whether Bosun is currently registered to launch at login. Always derived from the
    /// real registration state at call time -- see the interface remarks.</summary>
    bool IsEnabled();

    /// <summary>Registers Bosun to launch at login with
    /// <see cref="Bosun.UI.LaunchContextDetector.AutostartArgument"/>, targeting the currently
    /// running executable's own path. Idempotent and self-healing -- see the interface remarks.
    /// </summary>
    AutostartResult Enable();

    /// <summary>Removes the login registration, if any. A no-op (still
    /// <see cref="AutostartResult.Success"/>) if it was already absent.</summary>
    AutostartResult Disable();
}
