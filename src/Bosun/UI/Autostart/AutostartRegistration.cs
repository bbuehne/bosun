using Microsoft.Extensions.Logging;

namespace Bosun.UI.Autostart;

/// <summary>
/// The one implementation of <see cref="IAutostartRegistration"/> (bs-ojc.1 / E10a). Carries all
/// of the logic the acceptance criteria actually care about -- idempotency, self-healing against a
/// stale target, and fail-soft error handling -- over an injected <see cref="IAutostartStore"/>, so
/// this class is exercised in the default suite against an in-memory fake and never against the
/// real Run key. See <see cref="RegistryAutostartStore"/>'s remarks for why the Run key rather than
/// a <c>shell:startup</c> shortcut file.
/// </summary>
public sealed class AutostartRegistration : IAutostartRegistration
{
    /// <summary>
    /// How <see cref="IsEnabled"/> reads the <c>StartupApproved\Run</c> value (bs-ojc.2). The
    /// widely documented layout is 12 bytes: a 4-byte flag DWORD followed by an 8-byte FILETIME of
    /// when the user toggled it. Observed first bytes: <c>0x02</c> and <c>0x06</c> = enabled,
    /// <c>0x03</c> = disabled (<c>0x01</c> and <c>0x07</c> also appear in the literature as
    /// disabled). The pattern is bit 0 of the first byte: set = disabled, clear = enabled. We use
    /// that bit and ignore the rest. Source: community reverse-engineering of
    /// <c>StartupApproved</c>; Microsoft does not document the format.
    /// <para>
    /// Absent, EMPTY (no first byte), or non-binary data is treated as "no usable override" =
    /// enabled: that is Windows' own default for an entry with no override, and a malformed marker
    /// is not a user decision we can honour. The cost is a possible false "on"; the mitigation is
    /// that an explicit Bosun enable clears the value outright. A read that THROWS is different --
    /// we cannot tell what Windows will do, so <see cref="IsEnabled"/> fails soft to false.
    /// </para>
    /// </summary>
    private static bool OverrideMarksDisabled(byte[]? data) =>
        data is { Length: > 0 } && (data[0] & 0x01) != 0;

    private readonly IAutostartStore _store;
    private readonly Func<string?> _processPathProvider;
    private readonly ILogger<AutostartRegistration>? _logger;

    /// <param name="store">Where the registration actually lives. Inject a fake in tests; the real
    /// process wires <see cref="RegistryAutostartStore"/>.</param>
    /// <param name="processPathProvider">Returns the path to register as the launch target.
    /// Defaults to <see cref="Environment.ProcessPath"/> -- the RUNNING executable's own path,
    /// never a hardcoded location, per bs-ojc.1's brief ("this ships as a single file the user can
    /// put anywhere"). Overridable so tests can simulate a specific exe path without depending on
    /// the test host's own <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="logger">Optional; failures are logged, never thrown (ADR-012 fail-soft).</param>
    public AutostartRegistration(
        IAutostartStore store,
        Func<string?>? processPathProvider = null,
        ILogger<AutostartRegistration>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>Reads the store fresh on every call -- no cached field, no assumption that a
    /// prior <see cref="Enable"/>/<see cref="Disable"/> call in this process reflects current
    /// reality. That is the whole point: a stale in-memory belief is exactly the bug bs-ojc.1's
    /// acceptance criteria call out. True only if the Run value exists AND Windows' StartupApproved
    /// override (Task Manager / Settings > Startup) is absent or marks the entry enabled
    /// (bs-ojc.2); see <see cref="OverrideMarksDisabled"/> for the byte rule.</remarks>
    public bool IsEnabled()
    {
        try
        {
            if (_store.GetValue() is null)
            {
                return false;
            }

            return !OverrideMarksDisabled(_store.GetStartupApprovedData());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read autostart registration state");
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The write is UNCONDITIONAL: this does not first check whether a registration already
    /// exists, or whether an existing one already points at the right place. That is deliberate --
    /// it is what makes both "enable when already enabled" (a no-op in effect) and "enable when a
    /// stale registration points at an old path" (corrected) the same code path rather than two
    /// branches that could disagree. See <see cref="IAutostartRegistration"/>'s remarks.
    /// </remarks>
    public AutostartResult Enable()
    {
        string? exePath;
        try
        {
            exePath = _processPathProvider();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve the current executable path for autostart");
            return AutostartResult.Failed;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            _logger?.LogWarning("Cannot enable autostart: no current executable path is available.");
            return AutostartResult.Failed;
        }

        var commandLine = $"\"{exePath}\" {LaunchContextDetector.AutostartArgument}";

        try
        {
            _store.SetValue(commandLine);
            // bs-ojc.2 (maintainer decision, option B): an explicit enable in Bosun's tray also
            // clears a Task Manager / Settings "disabled" override, so startup is genuinely on.
            // If the clear throws we must not report Success while IsEnabled would say false, so
            // it falls into the same catch and returns Failed.
            _store.ClearStartupApproved();
            return AutostartResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enable autostart");
            return AutostartResult.Failed;
        }
    }

    /// <inheritdoc/>
    public AutostartResult Disable()
    {
        // Deliberately removes only the Run value and leaves any StartupApproved override alone:
        // that value is Windows' record of the user's own Task Manager choice, not ours, and
        // with no Run value it has no effect (IsEnabled is false regardless). Enable() clears it
        // when the user turns startup back on, so a leftover cannot resurrect a stale "disabled".
        // Deleting it here would add a second registry write that could fail after the first had
        // already succeeded, for no behavioural gain.
        try
        {
            _store.DeleteValue();
            return AutostartResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to disable autostart");
            return AutostartResult.Failed;
        }
    }
}
