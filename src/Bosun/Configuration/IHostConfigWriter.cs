namespace Bosun.Configuration;

/// <summary>
/// Writes host definitions back to <c>hosts.toml</c> (ADR-019). Bosun is the file's SOLE writer
/// once the window can edit hosts; the file stays fully hand-editable when Bosun is not running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation happens here, before the write, not after.</b> Every implementation must run
/// <see cref="ConfigValidator"/> against the resulting configuration and refuse to write if it
/// would be rejected. A UI that can save a config the loader will reject turns a startup error
/// into one the user caused and cannot see.
/// </para>
/// <para>
/// <b>The write must be atomic.</b> A crash mid-write must not leave a truncated
/// <c>hosts.toml</c> — that file is the only record of what the user asked for. Write to a temp
/// file in the same directory and replace.
/// </para>
/// <para>
/// <b>Bosun's own write must not be reprocessed as an external change.</b> <c>IHostConfigStore</c>
/// watches this file; writing it fires that watcher. That is the two-writer hazard ADR-006 refused
/// for Terminal's <c>settings.json</c>, aimed at ourselves. Handle it explicitly — suppress the
/// self-triggered reload, or make reload idempotent — never by relying on timing.
/// </para>
/// </remarks>
public interface IHostConfigWriter
{
    /// <summary>Adds a host, or replaces the existing one with the same
    /// <see cref="HostConfig.Key"/>.</summary>
    Task<HostConfigWriteResult> SaveHostAsync(HostConfig host, CancellationToken cancellationToken = default);

    /// <summary>Removes a host. A host that is currently mounted must be drained before its
    /// definition disappears — a drive letter cannot be left live with nothing describing it
    /// (Invariant I2). Whether this method drains or refuses is the implementation's contract to
    /// state, but it must not simply delete.</summary>
    Task<HostConfigWriteResult> DeleteHostAsync(string hostKey, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an attempted write. Never throws for the ordinary failure modes —
/// validation rejection and I/O trouble are both expected and are reported here so a form can
/// display them.</summary>
public sealed record HostConfigWriteResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Non-empty when the write was refused because the resulting config would not
    /// validate. These are the same errors <see cref="ConfigValidator"/> produces at load time.</summary>
    public IReadOnlyList<ConfigValidationError> ValidationErrors { get; init; } = [];

    /// <summary>Set when the write failed for a non-validation reason (I/O, permissions, a host
    /// that must be unmounted first). Human-readable and safe to show in the UI.</summary>
    public string? Error { get; init; }

    public static HostConfigWriteResult Ok() => new() { Succeeded = true };

    public static HostConfigWriteResult Invalid(IReadOnlyList<ConfigValidationError> errors) =>
        new() { Succeeded = false, ValidationErrors = errors };

    public static HostConfigWriteResult Failed(string error) =>
        new() { Succeeded = false, Error = error };
}
