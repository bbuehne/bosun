using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

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

    /// <summary>Replaces the history. Throws if it cannot be written -- the watchdog treats that as
    /// "cannot guarantee the limit" and does not restart.</summary>
    void Save(IReadOnlyList<DateTimeOffset> restarts);
}

/// <summary>
/// <see cref="IRestartHistoryStore"/> backed by a tiny JSON file, <c>watchdog-restarts.json</c>
/// beside <c>hosts.toml</c> (<c>%LOCALAPPDATA%\Bosun\</c>). The path is injected; tests use a temp
/// directory.
/// </summary>
/// <remarks>
/// A missing or corrupt file reads as empty history (logged): failing closed would disable recovery
/// for good over a damaged file. That cannot create a restart loop, because every restart rewrites
/// the file before it launches anything, so a corrupt file is replaced by the first restart after it.
/// Writes go to a temp file that is then moved over the target, so a crash mid-write cannot leave a
/// half-written file behind.
/// </remarks>
public sealed class JsonRestartHistoryStore(string path, ILogger<JsonRestartHistoryStore>? logger = null) : IRestartHistoryStore
{
    public const string DefaultFileName = "watchdog-restarts.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<DateTimeOffset> Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(path), JsonOptions);
            return document?.Restarts ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex, "The watchdog restart history at {Path} could not be read; treating it as empty", path);
            return [];
        }
    }

    public void Save(IReadOnlyList<DateTimeOffset> restarts)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new HistoryDocument { Restarts = [.. restarts] }, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private sealed class HistoryDocument
    {
        public List<DateTimeOffset> Restarts { get; set; } = [];
    }
}
