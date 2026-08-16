namespace Bosun.Hosting;

/// <summary>
/// Detects whether the configured <c>hosts.toml</c> exists and, if not, creates its containing
/// directory and writes a first-run template (ADR-012 Decision 4, bs-008). Behind an interface so
/// tests inject a fake filesystem root rather than touching the real <c>%LOCALAPPDATA%\Bosun</c>
/// (CLAUDE.md worktree-safety rules; <see cref="FirstRunConfigBootstrapper"/> is production-only).
/// </summary>
public interface IFirstRunConfigBootstrapper
{
    /// <summary>
    /// Ensures the directory containing <paramref name="configPath"/> exists -- unconditionally,
    /// whether or not the file itself is already there. This is also what guarantees
    /// <c>FileSystemConfigWatcher</c>'s constructor (which throws if its directory is missing)
    /// never observes a missing directory when driven through <see cref="StartupOrchestrator"/>:
    /// the orchestrator calls this before ever resolving <c>IHostConfigStore</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if no file existed at <paramref name="configPath"/> and a first-run
    /// template was just written there (first run). <see langword="false"/>, having touched
    /// nothing but the directory, if a file already existed.
    /// </returns>
    bool EnsureConfigExists(string configPath);
}
