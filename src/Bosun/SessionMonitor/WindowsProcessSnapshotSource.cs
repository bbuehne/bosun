using System.ComponentModel;
using System.Diagnostics;
using Bosun.SessionMonitor.Interop;

namespace Bosun.SessionMonitor;

/// <summary>
/// Real <see cref="IProcessSnapshotSource"/> (ADR-021, bs-vqh.7): <see cref="Process.GetProcesses()"/>
/// for the candidates, filtered by name, then <see cref="NativeProcessInfo"/> to enrich each with
/// image path, creation time and logon session.
/// </summary>
/// <remarks>
/// Construction is inert: nothing is enumerated and no Win32 call is made until
/// <see cref="Snapshot"/> or <see cref="CurrentLogonSession"/> is used, so registering it in DI is
/// safe from a worktree. Production only; the default suite touches the real source solely to
/// read its own process.
/// <para>
/// A process is omitted when its creation time cannot be read (it cannot be identified) or when
/// the process behind the PID no longer carries the requested image name (the PID was recycled
/// between enumeration and enrichment). Both are the normal race of enumerating a live system.
/// </para>
/// </remarks>
public sealed class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
    // The current process's logon session cannot change during its lifetime, so read it once.
    private readonly Lazy<LogonSessionId?> currentLogonSession =
        new(NativeProcessInfo.ReadCurrentLogonSession, LazyThreadSafetyMode.ExecutionAndPublication);

    public LogonSessionId? CurrentLogonSession => currentLogonSession.Value;

    public IReadOnlyList<ProcessSnapshotEntry> Snapshot(IReadOnlyCollection<string> imageNames)
    {
        ArgumentNullException.ThrowIfNull(imageNames);

        var wanted = imageNames
            .DistinctBy(StripExe, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(StripExe, n => n, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProcessSnapshotEntry>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!wanted.TryGetValue(process.ProcessName, out var imageName))
                    {
                        continue;
                    }

                    var entry = Enrich(process.Id, imageName);
                    if (entry is not null)
                    {
                        result.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Exited between enumeration and the property read: skip for this snapshot.
                }
            }
        }

        return result;
    }

    private static ProcessSnapshotEntry? Enrich(int pid, string imageName)
    {
        var facts = NativeProcessInfo.Read(pid);
        if (facts is not { CreationTimeUtc: { } creation })
        {
            return null;
        }

        if (!ImagePathMatches(facts.Value.ImagePath, imageName))
        {
            return null;
        }

        return new ProcessSnapshotEntry
        {
            Pid = pid,
            CreationTimeUtc = creation,
            ImageName = imageName,
            ImagePath = facts.Value.ImagePath,
            LogonSession = facts.Value.LogonSession,
        };
    }

    /// <summary>True when the image path's file name is <paramref name="imageName"/> (ignoring
    /// case and a missing <c>.exe</c>), or when the path is unknown and so cannot contradict it.</summary>
    internal static bool ImagePathMatches(string? imagePath, string imageName) =>
        imagePath is null
        || string.Equals(StripExe(System.IO.Path.GetFileName(imagePath)), StripExe(imageName), StringComparison.OrdinalIgnoreCase);

    internal static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
