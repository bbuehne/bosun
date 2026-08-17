using Bosun.Configuration;

namespace Bosun.UI.HostEditor;

/// <summary>Result of <see cref="HostEditorController.Build"/>: either a fully-formed
/// <see cref="HostConfig"/> ready to hand to <see cref="IHostConfigWriter.SaveHostAsync"/>, or the
/// client-side (parse-level) errors that stopped it -- never both.</summary>
public sealed record HostFormBuildResult
{
    public HostConfig? Host { get; init; }

    public IReadOnlyList<HostFormValidationError> Errors { get; init; } = [];

    public bool Succeeded => Host is not null && Errors.Count == 0;

    public static HostFormBuildResult Ok(HostConfig host) => new() { Host = host };

    public static HostFormBuildResult Invalid(IReadOnlyList<HostFormValidationError> errors) =>
        new() { Errors = errors };
}

/// <summary>Result of <see cref="HostEditorController.SaveAsync"/>.</summary>
public sealed record HostEditorSaveResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Best-effort per-field attribution, for highlighting the specific control. May be
    /// empty even when <see cref="GeneralError"/> is set -- see that property's remarks: nothing
    /// is ever dropped just because it did not map to a field.</summary>
    public IReadOnlyList<HostFormValidationError> FieldErrors { get; init; } = [];

    /// <summary>The complete, human-readable failure text -- every
    /// <see cref="ConfigValidationError.Message"/> the writer returned (not just the ones that
    /// mapped to a field), or the writer's own <see cref="HostConfigWriteResult.Error"/> for a
    /// non-validation failure (I/O, permissions). Always populated on failure, so a bug in
    /// <see cref="HostValidationFieldMapper"/>'s heuristic can never make an error silently
    /// disappear from the user's view -- it would just show up here without a field highlight.</summary>
    public string? GeneralError { get; init; }

    public static HostEditorSaveResult Ok() => new() { Succeeded = true };
}

/// <summary>Result of <see cref="HostEditorController.DeleteAsync"/>.</summary>
public sealed record HostEditorDeleteResult
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    public static HostEditorDeleteResult Ok() => new() { Succeeded = true };
}
