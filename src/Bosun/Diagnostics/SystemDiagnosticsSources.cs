using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Diag = System.Diagnostics;

namespace Bosun.Diagnostics;

// Real implementations of the diagnostics seams. Never constructed by the default test suite:
// they read the live process table, Event Log and drive list, and one launches Explorer.

public sealed class SystemProcessLister : IProcessLister
{
    public IReadOnlyList<int> GetProcessIds(string imageNameWithoutExtension)
    {
        var processes = Diag.Process.GetProcessesByName(imageNameWithoutExtension);
        try
        {
            return processes.Select(p => p.Id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// <see cref="DriveInfo.GetDrives"/> plus <see cref="DriveInfo.DriveType"/>, both of which are
/// <c>GetLogicalDrives</c>/<c>GetDriveType</c> and never touch the volume. Deliberately does not
/// read <c>IsReady</c>, the label or free space: those do real I/O, and on a drive pointing at a
/// dead host that I/O is the process-wide hang this whole tool exists to prevent.
/// </summary>
public sealed class SystemDriveLister : IDriveLister
{
    public IReadOnlyList<LogicalDriveEntry> GetDrives() =>
        DriveInfo.GetDrives().Select(d => new LogicalDriveEntry(d.Name, d.DriveType.ToString())).ToArray();
}

/// <summary>Reads the Application log with <see cref="EventLogReader"/>.</summary>
public sealed class ApplicationEventLogSource : IWindowsEventSource
{
    public IReadOnlyList<WindowsEventEntry> ReadApplicationEvents(
        DateTimeOffset sinceUtc, IReadOnlyCollection<int> eventIds)
    {
        var ids = string.Join(" or ", eventIds.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}"));
        var since = sinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var xpath = $"*[System[({ids}) and TimeCreated[@SystemTime>='{since}']]]";

        var events = new List<WindowsEventEntry>();
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                events.Add(new WindowsEventEntry(
                    record.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime()) : DateTimeOffset.MinValue,
                    record.Id,
                    record.ProviderName ?? string.Empty,
                    SafeLevel(record),
                    Describe(record)));
            }
        }

        return events;
    }

    private static string SafeLevel(EventRecord record)
    {
        try
        {
            return record.LevelDisplayName ?? record.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch (EventLogException)
        {
            return record.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    // FormatDescription needs the provider's message DLL and returns null or throws when it is
    // missing. The raw property values still name the program (Bosun.exe), which is what matters.
    private static string Describe(EventRecord record)
    {
        try
        {
            if (record.FormatDescription() is { Length: > 0 } formatted)
            {
                return formatted;
            }
        }
        catch (EventLogException)
        {
        }

        return string.Join(" | ", record.Properties.Select(p => p.Value?.ToString() ?? string.Empty));
    }
}

public sealed class AssemblyEnvironmentInfoSource : IEnvironmentInfoSource
{
    public EnvironmentInfo Get()
    {
        var assembly = typeof(AssemblyEnvironmentInfoSource).Assembly;
        return new EnvironmentInfo(
            assembly.GetName().Version?.ToString() ?? "unknown",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.VersionString}, {RuntimeInformation.OSArchitecture})",
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessId);
    }
}

/// <summary><c>explorer.exe /select,"path"</c>. Not exercised by any test: it opens a real window.</summary>
public sealed class ExplorerRevealer(ILogger<ExplorerRevealer> logger) : IExplorerRevealer
{
    public void Reveal(string filePath)
    {
        try
        {
            // /select,"path" must reach explorer as ONE argument; ArgumentList would quote the
            // whole thing and Explorer then opens the Documents folder instead.
            using var process = Diag.Process.Start(new Diag.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not reveal {Path} in Explorer", filePath);
        }
    }
}

/// <summary>A plain message box. Must be called on the UI thread, which a click handler already is.</summary>
public sealed class MessageBoxDiagnosticsErrorPresenter : IDiagnosticsErrorPresenter
{
    public void ShowError(string message) =>
        MessageBox.Show(message, "Bosun - Copy diagnostics", MessageBoxButton.OK, MessageBoxImage.Error);
}
