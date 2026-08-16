using Bosun.Configuration;

namespace Bosun.Terminal;

/// <summary>
/// Writes the Windows Terminal fragment for the current host set (E7c, bs-k41;
/// docs/ARCHITECTURE.md §3). The one thing this interface deliberately does NOT do is decide WHEN
/// to write -- callers (today, direct callers and tests; see <see cref="FragmentRewriteCoordinator"/>
/// for the "rewrite on every config change" behaviour) own that.
/// </summary>
public interface IFragmentWriter
{
    /// <summary>
    /// Generates the fragment document for every host in <paramref name="config"/>, validates the
    /// JSON it is about to write, and writes it atomically to the configured fragment path.
    /// </summary>
    Task WriteAsync(BosunConfig config, CancellationToken cancellationToken = default);
}
