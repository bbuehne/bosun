using System.IO;

namespace Bosun.Terminal;

/// <summary>
/// Where <see cref="FragmentWriter"/> writes the fragment. Always explicit and injectable -- tests
/// must be able to point it at a temp directory and never the real path (CLAUDE.md worktree-safety
/// rules; bs-k41's acceptance criteria).
/// </summary>
public sealed record FragmentWriterOptions
{
    /// <summary>Full path to the fragment file itself, e.g. <c>...\Bosun\bosun.json</c>. The
    /// containing directory is created if absent (the installer is expected to normally do this,
    /// per bs-k41, but <see cref="FragmentWriter"/> does not assume it).</summary>
    public required string FragmentPath { get; init; }

    /// <summary>
    /// The verified real-world path (bs-3ir, primary-sourced from Microsoft Learn):
    /// <c>%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json</c>. Note the
    /// <c>Microsoft\</c> path segment -- ADR-006 and CLAUDE.md Invariant I5 both abbreviate it away
    /// in prose, but the real path includes it. The Store-vs-unpackaged distinction ADR-006's
    /// implementation note warns about turned out to be a red herring for an unpackaged win-x64 EXE
    /// like Bosun: both the per-user and all-users Fragments directories are read regardless of how
    /// Terminal itself was installed; that distinction only applies to Store-PACKAGED apps
    /// registering via an app-extension manifest, a different mechanism Bosun does not use.
    /// </summary>
    public static FragmentWriterOptions CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fragmentPath = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", "Bosun", "bosun.json");
        return new FragmentWriterOptions { FragmentPath = fragmentPath };
    }
}
