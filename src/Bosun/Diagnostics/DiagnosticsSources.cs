namespace Bosun.Diagnostics;

// The seams the diagnostics bundle reads the machine through (bs-ds3). Every one is read-only and
// every one is faked in the default test suite: nothing here may reach the real process table,
// Event Log, drive list, or Explorer from a test.

/// <summary>Finds processes by image name.</summary>
public interface IProcessLister
{
    /// <summary>PIDs of every running process whose image name (without <c>.exe</c>) is
    /// <paramref name="imageNameWithoutExtension"/>, such as <c>rclone</c>.</summary>
    IReadOnlyList<int> GetProcessIds(string imageNameWithoutExtension);
}

/// <summary>One logical drive and its type, as the OS reports it.</summary>
public sealed record LogicalDriveEntry(string Name, string DriveType);

/// <summary>Lists the logical drives. Must not touch the volumes: asking a wedged network drive
/// whether it is ready, or for its label, is exactly the call that hangs a process (I2).</summary>
public interface IDriveLister
{
    IReadOnlyList<LogicalDriveEntry> GetDrives();
}

/// <summary>One Application-log event.</summary>
public sealed record WindowsEventEntry(
    DateTimeOffset TimeCreatedUtc, int EventId, string Provider, string Level, string Message);

/// <summary>Reads the Application event log.</summary>
public interface IWindowsEventSource
{
    /// <summary>Events with one of <paramref name="eventIds"/> logged at or after
    /// <paramref name="sinceUtc"/>. May throw; the caller records that and carries on.</summary>
    IReadOnlyList<WindowsEventEntry> ReadApplicationEvents(DateTimeOffset sinceUtc, IReadOnlyCollection<int> eventIds);
}

/// <summary>What <c>version.txt</c> says about this process and machine.</summary>
public sealed record EnvironmentInfo(
    string AssemblyVersion,
    string InformationalVersion,
    string OsDescription,
    string RuntimeDescription,
    int CurrentProcessId);

public interface IEnvironmentInfoSource
{
    EnvironmentInfo Get();
}

/// <summary>Shows a file in Explorer. Behind an interface so a test never launches Explorer.</summary>
public interface IExplorerRevealer
{
    /// <summary>Opens Explorer with <paramref name="filePath"/> selected. Best effort; never throws.</summary>
    void Reveal(string filePath);
}

/// <summary>Tells the user something went wrong. Behind an interface so a test never shows a dialog.</summary>
public interface IDiagnosticsErrorPresenter
{
    void ShowError(string message);
}
