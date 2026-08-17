using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Bosun.UI;

/// <summary>
/// Real <see cref="IWindowPlacementStore"/>: one small JSON file. The file path is always
/// explicit and injected -- never hardcoded -- so tests can point it at a temp directory and
/// never touch the real <c>%LOCALAPPDATA%</c> location (CLAUDE.md worktree-safety rules), the
/// same pattern <see cref="Hosting.BosunHostOptions"/> already establishes for logs and config.
/// </summary>
public sealed class JsonWindowPlacementStore : IWindowPlacementStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonWindowPlacementStore>? _logger;

    public JsonWindowPlacementStore(string filePath, ILogger<JsonWindowPlacementStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Bosun\window-state.json</c> -- symmetric with
    /// <see cref="Hosting.BosunHostOptions.CreateDefault"/>'s <c>hosts.toml</c> and log directory
    /// (ADR-012 Decision 4), so docs/OPERATIONS.md can keep saying everything Bosun writes lives
    /// under one <c>%LOCALAPPDATA%\Bosun</c> directory.
    /// </summary>
    public static string GetDefaultFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Bosun", "window-state.json");
    }

    public WindowPlacement? TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<WindowPlacement>(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(
                ex, "Failed to load persisted window placement from {Path}; using the default placement instead", _filePath);
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(placement));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to persist window placement to {Path}", _filePath);
        }
    }
}
