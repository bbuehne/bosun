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
    /// <summary>
    /// How long to wait before each re-attempt of the final replace, when the destination is
    /// transiently unavailable (see <see cref="AtomicWriteAsync"/>). Bounded: 5 retries, ~385 ms in
    /// total, which is well inside what a person clicking Save notices, and far short of masking a
    /// destination that is locked for real (that still fails, with the original file intact).
    /// </summary>
    private static readonly TimeSpan[] ReplaceRetryDelays =
    [
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    private readonly string _path;
    private readonly HostConfigStore _store;
    private readonly Func<string, bool>? _identityFileExists;
    private readonly TimeProvider _timeProvider;

    /// <param name="path">Path to <c>hosts.toml</c> -- must be the same path
    /// <paramref name="store"/> was loaded from and watches.</param>
    /// <param name="store">The store this writer coordinates with to avoid the two-writer hazard
    /// (see class remarks). Its <see cref="HostConfigStore.Current"/> is the base every write is
    /// built from.</param>
    /// <param name="identityFileExists">Passed straight through to <see cref="ConfigValidator.Validate"/>.
    /// Production callers may omit it (defaults to a real <see cref="File.Exists(string)"/> check);
    /// tests should pass whatever fake the same-process <see cref="HostConfigStore"/> was given, so
    /// validation here agrees with what the store would decide on its own next reload.</param>
    /// <param name="timeProvider">Used only for the short waits between re-attempts of the final
    /// replace. Defaults to <see cref="TimeProvider.System"/>; tests inject one so no test ever
    /// really waits.</param>
    public HostConfigWriter(
        string path,
        HostConfigStore store,
        Func<string, bool>? identityFileExists = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(store);

        _path = path;
        _store = store;
        _identityFileExists = identityFileExists;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            await AtomicWriteAsync(text, cancellationToken).ConfigureAwait(false);
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
    /// Writes <paramref name="text"/> to a temp file in the target's own directory, then
    /// atomically replaces the target with it. On Windows/NTFS,
    /// <see cref="File.Move(string, string, bool)"/> with <c>overwrite: true</c> between two paths
    /// on the same volume is a single filesystem rename (<c>MoveFileEx</c> with
    /// <c>MOVEFILE_REPLACE_EXISTING</c>) -- a crash before it starts leaves the old
    /// <c>hosts.toml</c> completely intact, and a crash after it completes leaves the new one
    /// completely intact. There is no window in which <c>hosts.toml</c> itself is observed
    /// truncated or partially written (IHostConfigWriter's "the write must be atomic";
    /// <c>hosts.toml</c> "is the only record of what the user asked for").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The replace step is retried briefly (bs-8e6).</b> Replacing an EXISTING file fails with
    /// <see cref="UnauthorizedAccessException"/> ("Access to the path is denied", 0x80070005) if
    /// any other process holds a handle on the destination that does not allow delete-sharing --
    /// which is what an antivirus scanner or the search indexer does to a file that was just
    /// written, including one the user's editor just saved. The hold lasts milliseconds. Measured
    /// in isolation, outside the test suite, with every core saturated: replacing an existing
    /// file failed on its first attempt 11 times in 60,000 and every one succeeded on the next
    /// try about a millisecond later, while a plain rename to a path with no existing file failed
    /// 0 in 60,000. Without a retry, one save in thousands would tell the user "could not write
    /// hosts.toml: Access to the path is denied" for a file that is perfectly writable.
    /// </para>
    /// <para>
    /// Only the replace is retried, never the temp-file write (failing to create the temp file is
    /// a real permission problem, not a transient hold), and only a bounded number of times. A
    /// destination that is genuinely locked still fails, with the original file untouched.
    /// </para>
    /// </remarks>
    private async Task AtomicWriteAsync(string text, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($"'{_path}' has no containing directory.");

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(tempPath, text, cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, fullPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (
                    attempt < ReplaceRetryDelays.Length
                    && ex is IOException or UnauthorizedAccessException
                    && ex is not (FileNotFoundException or DirectoryNotFoundException))
                {
                    await Task.Delay(ReplaceRetryDelays[attempt], _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
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
