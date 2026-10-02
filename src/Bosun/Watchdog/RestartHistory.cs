using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

/// <summary>
/// The most recent automatic restart, and why (bs-aoz). Written by the instance that is about to
/// restart, read by the one it launches, which tells the user what happened.
/// </summary>
/// <param name="At">When the restart was requested, by the old instance's clock.</param>
/// <param name="Reason">The watchdog's own reason string, e.g. "supervisor loop stalled: exited, no
/// activity for 3 min 0 s". Shown to the user as the cause, so it is written for a person.</param>
public sealed record LastRestart(DateTimeOffset At, string Reason);

/// <summary>
/// Where the watchdog remembers when it last restarted Bosun (bs-6to). The history has to outlive
/// the restart it records: the restart replaces the process, and the new process must know the
/// previous ones happened or the "3 per hour" limit would reset every time and never bind.
/// </summary>
public interface IRestartHistoryStore
{
    /// <summary>The recorded restart times. Empty if there is no history or it cannot be read; never
    /// throws.</summary>
    IReadOnlyList<DateTimeOffset> Load();

    /// <summary>The most recent automatic restart and its reason, or <see langword="null"/> if none is
    /// recorded (no file, a file from before reasons were kept, or a file that cannot be read). Never
    /// throws.</summary>
    LastRestart? LoadLast();

    /// <summary>Replaces the history. Throws if it cannot be written -- the watchdog treats that as
    /// "cannot guarantee the limit" and does not restart. <paramref name="last"/> replaces the recorded
    /// reason; <see langword="null"/> leaves no reason recorded.</summary>
    void Save(IReadOnlyList<DateTimeOffset> restarts, LastRestart? last = null);
}

/// <summary>
/// <see cref="IRestartHistoryStore"/> backed by a tiny JSON file, <c>watchdog-restarts.json</c>
/// beside <c>hosts.toml</c> (<c>%LOCALAPPDATA%\Bosun\</c>). The path is injected; tests use a temp
/// directory.
/// </summary>
/// <remarks>
/// <para>
/// A missing or corrupt file reads as empty history (logged): failing closed would disable recovery
/// for good over a damaged file. That cannot create a restart loop, because every restart rewrites
/// the file before it launches anything, so a corrupt file is replaced by the first restart after it.
/// Writes go to a temp file that is then moved over the target, so a crash mid-write cannot leave a
/// half-written file behind.
/// </para>
/// <para>
/// <b>Format (bs-aoz).</b> <c>{ "Restarts": [ ...timestamps... ], "Last": { "At": ..., "Reason": ... } }</c>
/// (the serializer's default, PascalCase names, as before). <c>Last</c> is new; a file without it
/// (written before bs-aoz) still loads, with no recorded reason. The reverse also holds:
/// <c>Restarts</c> is unchanged, so an older build reading a newer file ignores <c>Last</c> and still
/// enforces the limit.
/// </para>
/// </remarks>
public sealed class JsonRestartHistoryStore(string path, ILogger<JsonRestartHistoryStore>? logger = null) : IRestartHistoryStore
{
    public const string DefaultFileName = "watchdog-restarts.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<DateTimeOffset> Load() => Read()?.Restarts ?? [];

    public LastRestart? LoadLast() => Read()?.Last is { } last && !string.IsNullOrWhiteSpace(last.Reason)
        ? new LastRestart(last.At, last.Reason)
        : null;

    public void Save(IReadOnlyList<DateTimeOffset> restarts, LastRestart? last = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new HistoryDocument
        {
            Restarts = [.. restarts],
            Last = last is null ? null : new LastDocument { At = last.At, Reason = last.Reason },
        };

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private HistoryDocument? Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex, "The watchdog restart history at {Path} could not be read; treating it as empty", path);
            return null;
        }
    }

    private sealed class HistoryDocument
    {
        public List<DateTimeOffset> Restarts { get; set; } = [];

        public LastDocument? Last { get; set; }
    }

    private sealed class LastDocument
    {
        public DateTimeOffset At { get; set; }

        public string Reason { get; set; } = string.Empty;
    }
}
