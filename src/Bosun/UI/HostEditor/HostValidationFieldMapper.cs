using Bosun.Configuration;

namespace Bosun.UI.HostEditor;

/// <summary>
/// Maps <see cref="ConfigValidationError"/>s returned by <see cref="IHostConfigWriter.SaveHostAsync"/>
/// back onto the specific form field(s) they concern (bs-ww9.8: "the user needs to know *which*
/// field"). Pure string matching against <see cref="ConfigValidationError.Rule"/> and
/// <see cref="ConfigValidationError.Message"/> -- <see cref="ConfigValidator"/> produces both a
/// stable rule id and a human message, and this is exactly the kind of "which host does this
/// belong to" question the rule id alone does not answer for the two whole-config rules
/// (duplicate-display-name, drive-collision), so the message's host-key list is parsed too.
/// </summary>
public static class HostValidationFieldMapper
{
    /// <summary>Maps every error in <paramref name="errors"/> that concerns
    /// <paramref name="hostKey"/> onto one or more <see cref="HostFormFieldId"/>s. Errors that do
    /// not concern this host at all (in principle should not happen -- the writer validates the
    /// whole config, but only this host's edit could plausibly have caused a failure) are silently
    /// excluded from the returned list; callers must still surface the complete, unfiltered set
    /// separately (see <see cref="HostEditorSaveResult.GeneralError"/>) so a gap in this mapping
    /// can never hide an error from the user.</summary>
    public static IReadOnlyList<HostFormValidationError> Map(
        IReadOnlyList<ConfigValidationError> errors, string hostKey)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKey);

        var mapped = new List<HostFormValidationError>();

        foreach (var error in errors)
        {
            if (!ConcernsHost(error, hostKey))
            {
                continue;
            }

            foreach (var field in FieldsFor(error.Rule))
            {
                mapped.Add(new HostFormValidationError(field, error.Message));
            }
        }

        return mapped;
    }

    /// <summary>True if <paramref name="error"/> is about <paramref name="hostKey"/> specifically.
    /// Every per-host rule in <see cref="ConfigValidator"/> writes its message with a
    /// <c>"hosts.&lt;key&gt;: "</c> prefix; the two whole-config rules (duplicate-display-name,
    /// drive-collision) instead list every colliding host's key in a trailing
    /// <c>"(key1, key2, ...)"</c>.</summary>
    private static bool ConcernsHost(ConfigValidationError error, string hostKey)
    {
        if (error.Message.StartsWith($"hosts.{hostKey}:", StringComparison.Ordinal))
        {
            return true;
        }

        var openParen = error.Message.LastIndexOf('(');
        var closeParen = error.Message.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
        {
            return false;
        }

        var keys = error.Message[(openParen + 1)..closeParen].Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return keys.Contains(hostKey, StringComparer.Ordinal);
    }

    private static IReadOnlyList<HostFormFieldId> FieldsFor(string rule) => rule switch
    {
        "mount-missing-drive-or-remote-path" => [HostFormFieldId.Drive, HostFormFieldId.RemotePath],
        "invalid-drive-letter" => [HostFormFieldId.Drive],
        "invalid-vfs-cache-mode" => [HostFormFieldId.VfsCacheMode],
        "invalid-network-mode" => [HostFormFieldId.NetworkMode],
        "negative-idle-unmount-seconds" => [HostFormFieldId.IdleUnmountSeconds],
        "identity-file-not-found" => [HostFormFieldId.IdentityFile],
        "negative-probe-interval" => [HostFormFieldId.ProbeIntervalSeconds],
        "tmux-requires-session" => [HostFormFieldId.TmuxSession],
        "duplicate-display-name" => [HostFormFieldId.DisplayName],
        "drive-collision" => [HostFormFieldId.Drive],
        _ => [HostFormFieldId.General],
    };
}
