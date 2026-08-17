using System.IO;

namespace Bosun.Configuration;

/// <summary>
/// Default <see cref="IHostConfigWriter"/> (bs-ww9.8, ADR-019). Builds the candidate
/// <see cref="BosunConfig"/> from <see cref="HostConfigStore.Current"/> plus the requested
/// change, validates it with the exact same <see cref="ConfigValidator"/> the loader uses,
/// serializes it back to TOML (<see cref="HostConfigTomlWriter"/>), and writes it atomically
/// (temp file in the same directory, then an atomic rename over the original).
/// </summary>
/// <remarks>
/// <para>
/// <b>Depends on the CONCRETE <see cref="HostConfigStore"/>, not just <see cref="IHostConfigStore"/>.</b>
/// Resolving the two-writer hazard ADR-019 calls out (this store's own file watcher reacting to
/// this writer's own write) needs a coordination point neither <see cref="IHostConfigStore"/> nor
/// <see cref="IHostConfigWriter"/> exposes, and those two interfaces are load-bearing seams other
/// components fake in tests -- adding a member to either would force every existing
/// <c>IHostConfigStore</c> fake in the suite to grow a no-op implementation, including ones under
/// <c>tests/Bosun.Tests/**/Independent/</c> that this change must not touch. <see
/// cref="HostConfigStore.AdoptSelfWrite"/> is <see langword="internal"/> instead (visible via
/// <c>InternalsVisibleTo("Bosun.Tests")</c>, same as the rest of this project's internal-seam
/// tests) and this class is the one and only caller.
/// </para>
/// <para>
/// <b>Deleting a mounted host.</b> This writer does not own <c>IMountSupervisor</c> and must not
/// call it -- only <c>IMountSupervisor</c> may call <c>mount/mount</c>/<c>mount/unmount</c>, and
/// draining a host is itself an unmount. So <see cref="DeleteHostAsync"/> does not drain, and does
/// not itself check whether a host is currently mounted (it has no way to: mount state lives in
/// <c>MountSupervisor</c>, not in <see cref="BosunConfig"/>). <b>The contract is that the caller
/// drains first</b>: request an unmount (<c>IMountSupervisor.RequestUnmountAsync</c>) and wait for
/// the host to leave <c>MountState.Mounting</c>/<c>Mounted</c>/<c>Draining</c> (via
/// <c>IMountSupervisor.GetSnapshot()</c>) before calling <see cref="DeleteHostAsync"/>. This keeps
/// Configuration and Supervisor from depending on each other in both directions, at the cost of
/// pushing the sequencing into whichever caller owns both (the tray UI's host-editor
/// view-model) -- see the delivery report for why the alternative (this writer polling or querying
/// supervisor state itself) was rejected.
/// </para>
/// </remarks>
public sealed class HostConfigWriter : IHostConfigWriter
{
    private readonly string _path;
    private readonly HostConfigStore _store;
    private readonly Func<string, bool>? _identityFileExists;

    /// <param name="path">Path to <c>hosts.toml</c> -- must be the same path
    /// <paramref name="store"/> was loaded from and watches.</param>
    /// <param name="store">The store this writer coordinates with to avoid the two-writer hazard
    /// (see class remarks). Its <see cref="HostConfigStore.Current"/> is the base every write is
    /// built from.</param>
    /// <param name="identityFileExists">Passed straight through to <see cref="ConfigValidator.Validate"/>.
    /// Production callers may omit it (defaults to a real <see cref="File.Exists(string)"/> check);
    /// tests should pass whatever fake the same-process <see cref="HostConfigStore"/> was given, so
    /// validation here agrees with what the store would decide on its own next reload.</param>
    public HostConfigWriter(string path, HostConfigStore store, Func<string, bool>? identityFileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(store);

        _path = path;
        _store = store;
        _identityFileExists = identityFileExists;
    }

    public Task<HostConfigWriteResult> SaveHostAsync(HostConfig host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var current = _store.Current;
        var hosts = new Dictionary<string, HostConfig>(current.Hosts, StringComparer.Ordinal)
        {
            [host.Key] = host,
        };
        var candidate = current with { Hosts = hosts };

        return WriteAsync(candidate, cancellationToken);
    }

    /// <remarks>
    /// See the class remarks: this method trusts the caller has already drained the host if it
    /// was mounted. It does not check live mount state and does not call
    /// <c>IMountSupervisor</c> -- it removes the host from the candidate config, validates, and
    /// writes, exactly like <see cref="SaveHostAsync"/> does for an add/edit.
    /// </remarks>
    public Task<HostConfigWriteResult> DeleteHostAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKey);

        var current = _store.Current;
        if (!current.Hosts.ContainsKey(hostKey))
        {
            return Task.FromResult(HostConfigWriteResult.Failed($"host '{hostKey}' is not configured"));
        }

        var hosts = new Dictionary<string, HostConfig>(current.Hosts, StringComparer.Ordinal);
        hosts.Remove(hostKey);
        var candidate = current with { Hosts = hosts };

        return WriteAsync(candidate, cancellationToken);
    }

    private async Task<HostConfigWriteResult> WriteAsync(BosunConfig candidate, CancellationToken cancellationToken)
    {
        // ADR-019 Decision 3 / IHostConfigWriter's remarks: validate BEFORE writing a byte. The
        // exact same gate HostConfigStore uses at load time, so nothing this method writes can be
        // rejected by the loader on next start.
        var validation = ConfigValidator.Validate(candidate, _identityFileExists);
        if (!validation.IsValid)
        {
            return HostConfigWriteResult.Invalid(validation.Errors);
        }

        var text = HostConfigTomlWriter.Write(candidate);

        try
        {
            await AtomicWriteAsync(_path, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HostConfigWriteResult.Failed($"could not write '{_path}': {ex.Message}");
        }

        // Must happen only after the write to disk actually succeeds -- adopting the config in
        // memory before the bytes are safely on disk would let Current say one thing while a
        // crash leaves hosts.toml saying another.
        _store.AdoptSelfWrite(candidate, text);

        return HostConfigWriteResult.Ok();
    }

    /// <summary>
    /// Writes <paramref name="text"/> to a temp file in <paramref name="path"/>'s own directory,
    /// then atomically replaces <paramref name="path"/> with it. On Windows/NTFS,
    /// <see cref="File.Move(string, string, bool)"/> with <c>overwrite: true</c> between two paths
    /// on the same volume is a single filesystem rename (<c>MoveFileEx</c> with
    /// <c>MOVEFILE_REPLACE_EXISTING</c>) -- a crash before it starts leaves the old
    /// <c>hosts.toml</c> completely intact, and a crash after it completes leaves the new one
    /// completely intact. There is no window in which <c>hosts.toml</c> itself is observed
    /// truncated or partially written (IHostConfigWriter's "the write must be atomic";
    /// <c>hosts.toml</c> "is the only record of what the user asked for").
    /// </summary>
    private static async Task AtomicWriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($"'{path}' has no containing directory.");

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(tempPath, text, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            // Only reached without a completed Move if WriteAllTextAsync or Move itself threw --
            // on the success path the temp file no longer exists under tempPath (Move renamed it
            // away), so this is a no-op there.
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
