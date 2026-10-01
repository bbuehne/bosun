using Bosun.Diagnostics;
using Bosun.Health;

namespace Bosun.Tests.Diagnostics.Fakes;

// Every seam the diagnostics bundle reads through, faked. Nothing here touches the real process
// table, Event Log, drive list, Explorer or a dialog.

internal sealed class FakeAppHealth(AppHealth value) : IAppHealth
{
    public AppHealth Value { get; set; } = value;

    public Exception? Throws { get; set; }

    public AppHealth Current => Throws is { } ex ? throw ex : Value;

    public event EventHandler<AppHealthChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class FakeProcessLister : IProcessLister
{
    private readonly Dictionary<string, int[]> _byImage = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Throws { get; set; }

    public void Set(string image, params int[] pids) => _byImage[image] = pids;

    public IReadOnlyList<int> GetProcessIds(string imageNameWithoutExtension) =>
        Throws is { } ex ? throw ex : _byImage.TryGetValue(imageNameWithoutExtension, out var pids) ? pids : [];
}

internal sealed class FakeDriveLister : IDriveLister
{
    public List<LogicalDriveEntry> Drives { get; } = [];

    public Exception? Throws { get; set; }

    public IReadOnlyList<LogicalDriveEntry> GetDrives() => Throws is { } ex ? throw ex : Drives;
}

internal sealed class FakeWindowsEventSource : IWindowsEventSource
{
    public List<WindowsEventEntry> Events { get; } = [];

    public Exception? Throws { get; set; }

    public DateTimeOffset? LastSince { get; private set; }

    public IReadOnlyList<WindowsEventEntry> ReadApplicationEvents(DateTimeOffset sinceUtc, IReadOnlyCollection<int> eventIds)
    {
        LastSince = sinceUtc;
        return Throws is { } ex ? throw ex : Events;
    }
}

internal sealed class FakeEnvironmentInfoSource : IEnvironmentInfoSource
{
    public EnvironmentInfo Info { get; set; } = new("1.2.3.4", "1.2.3+abc123", "Windows 11 test", ".NET 10 test", 4242);

    public EnvironmentInfo Get() => Info;
}

internal sealed class FakeExplorerRevealer : IExplorerRevealer
{
    public List<string> Revealed { get; } = [];

    public Exception? Throws { get; set; }

    public void Reveal(string filePath)
    {
        Revealed.Add(filePath);
        if (Throws is { } ex)
        {
            throw ex;
        }
    }
}

internal sealed class FakeErrorPresenter : IDiagnosticsErrorPresenter
{
    public List<string> Errors { get; } = [];

    public Exception? Throws { get; set; }

    public void ShowError(string message)
    {
        Errors.Add(message);
        if (Throws is { } ex)
        {
            throw ex;
        }
    }
}

internal sealed class FakeBundleBuilder : IDiagnosticsBundleBuilder
{
    public int Calls { get; private set; }

    public Exception? Throws { get; set; }

    public string Path { get; set; } = @"C:\fake\bosun-diagnostics-20261001-090000.zip";

    /// <summary>When set, the build does not finish until this completes.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<DiagnosticsBundleResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Gate is not null)
        {
            await Gate.Task;
        }

        return Throws is { } ex ? throw ex : new DiagnosticsBundleResult(Path, []);
    }
}
