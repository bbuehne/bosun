using System.IO;
using Bosun.UI;

namespace Bosun.Tests.UI;

/// <summary>
/// Real file I/O against a temp directory -- never the real <c>%LOCALAPPDATA%</c> path (CLAUDE.md
/// worktree-safety rules; the real path is only ever used via <see cref="JsonWindowPlacementStore.GetDefaultFilePath"/>,
/// which nothing here calls).
/// </summary>
public sealed class JsonWindowPlacementStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonWindowPlacementStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "bosun-tests-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_tempDirectory, "window-state.json");

    [Fact]
    public void TryLoad_ReturnsNull_WhenNoFileExistsYet()
    {
        var store = new JsonWindowPlacementStore(FilePath);

        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Save_ThenTryLoad_RoundTripsThePlacement()
    {
        var store = new JsonWindowPlacementStore(FilePath);
        var placement = new WindowPlacement { Left = 12, Top = 34, Width = 800, Height = 600, IsMaximized = true };

        store.Save(placement);
        var loaded = store.TryLoad();

        Assert.Equal(placement, loaded);
    }

    [Fact]
    public void Save_CreatesTheDirectory_WhenItDoesNotExist()
    {
        Assert.False(Directory.Exists(_tempDirectory));

        var store = new JsonWindowPlacementStore(FilePath);
        store.Save(new WindowPlacement { Left = 0, Top = 0, Width = 800, Height = 600, IsMaximized = false });

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void TryLoad_ReturnsNull_ForCorruptJson_RatherThanThrowing()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(FilePath, "{ not valid json ");

        var store = new JsonWindowPlacementStore(FilePath);

        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Constructor_Throws_ForANullOrEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new JsonWindowPlacementStore(""));
    }

    [Fact]
    public void GetDefaultFilePath_IsUnderLocalAppDataBosun()
    {
        var path = JsonWindowPlacementStore.GetDefaultFilePath();

        Assert.Contains("Bosun", path);
        Assert.EndsWith("window-state.json", path);
    }
}
