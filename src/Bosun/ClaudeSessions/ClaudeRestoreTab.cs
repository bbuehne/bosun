namespace Bosun.ClaudeSessions;

/// <summary>
/// One Claude Code session to reopen in a Windows Terminal tab: the input to
/// <see cref="WtRestoreArgumentBuilder"/> (ADR-021 Decision 6). This is deliberately the builder's
/// own input type, not the on-disk session record -- E13c defines that record, and E13g maps
/// dispatched records onto this shape. Keeping the two apart means the builder stays a pure
/// function that knows nothing about record storage.
/// </summary>
/// <param name="SessionId">The Claude Code session id passed to <c>claude --resume</c>.
/// Must be non-empty and contain no whitespace and no <c>;</c>.</param>
/// <param name="Cwd">The session's recorded working directory; becomes the tab's <c>-d</c>.</param>
/// <param name="Title">The session title, when Claude Code set one. Null or blank means the
/// builder falls back to the cwd's folder name.</param>
/// <param name="ProfileId">The recorded <c>WT_PROFILE_ID</c>. Null or blank means the builder
/// omits <c>-p</c>, so Terminal opens its default profile.</param>
/// <param name="TabRootImagePath">Image path of the tab's root process (the shell Terminal started
/// for the tab). Null or blank is treated like an unknown root: no wrapper.</param>
/// <param name="ClaudeImagePath">Image path of the Claude Code process itself. Used only when no
/// shell wrapper applies, because <c>wt.exe</c> cannot launch the npm <c>claude.cmd</c> shim by
/// name.</param>
public sealed record ClaudeRestoreTab(
    string SessionId,
    string Cwd,
    string? Title,
    string? ProfileId,
    string? TabRootImagePath,
    string ClaudeImagePath);
