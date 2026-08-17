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
    /// <remarks>Reads <see cref="IAutostartStore.GetValue"/> fresh on every call -- no cached
    /// field, no assumption that a prior <see cref="Enable"/>/<see cref="Disable"/> call in this
    /// process reflects current reality. That is the whole point: a stale in-memory belief is
    /// exactly the bug bs-ojc.1's acceptance criteria call out.</remarks>
    public bool IsEnabled()
    {
        try
        {
            return _store.GetValue() is not null;
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
