using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Bosun.SessionMonitor;

/// <summary>One live <c>ssh.exe</c> process, before correlation to a configured host (bs-8dr).
/// <see cref="TargetHost"/> is whatever <see cref="SshCommandLineParser"/> extracted from the
/// process's command line -- it may be <see langword="null"/>, or may not match any configured
/// host key; neither is this type's concern.</summary>
public sealed record SshProcessInfo
{
    public required int ProcessId { get; init; }
    public string? TargetHost { get; init; }
    public required DateTimeOffset StartTime { get; init; }
}

/// <summary>Enumerates live <c>ssh.exe</c> processes and their parsed target host (bs-8dr).
/// Deliberately host-agnostic -- it knows nothing about <c>hosts.toml</c>; correlation to a
/// configured host happens one layer up, in <see cref="SshSessionMonitor"/>.</summary>
public interface ISshProcessEnumerator
{
    IReadOnlyList<SshProcessInfo> Enumerate();
}

/// <summary>
/// Real <see cref="ISshProcessEnumerator"/> (bs-8dr): <see cref="Process.GetProcessesByName"/>
/// for the process list, then a single bulk CIM query for every <c>ssh.exe</c> command line --
/// not one query per process. E9 polls this to paint the tray, so a query-per-process would
/// multiply badly; measured performance is the whole point of the bulk query.
/// </summary>
/// <remarks>
/// Handles what will actually happen in practice, per bs-8dr:
/// <list type="bullet">
/// <item>A process exiting between <see cref="Process.GetProcessesByName"/> and the property
/// read that follows (<see cref="Win32Exception"/> / <see cref="InvalidOperationException"/> /
/// <see cref="ArgumentException"/>) -- skipped for this tick, not an error.</item>
/// <item>Access denied on a process owned by another user or an elevated session -- same
/// handling, same reasoning.</item>
/// <item>CIM being slow, unavailable, or access-denied -- caught around the whole query. A CIM
/// failure degrades to "no command lines resolved this tick" (every process reports
/// <c>TargetHost = null</c> and is filtered out upstream), never a crash.</item>
/// </list>
/// Production only. Enumeration itself is not unit-tested against real processes (bs-8dr
/// acceptance) -- see <see cref="SshCommandLineParser"/> for the part that is.
/// </remarks>
public sealed class CimSshProcessEnumerator(ILogger<CimSshProcessEnumerator> logger) : ISshProcessEnumerator
{
    private const string ProcessName = "ssh";

    public IReadOnlyList<SshProcessInfo> Enumerate()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            if (processes.Length == 0)
            {
                return [];
            }

            var commandLines = QueryCommandLines(logger);
            var results = new List<SshProcessInfo>(processes.Length);

            foreach (var process in processes)
            {
                if (TryDescribe(process, commandLines, out var info))
                {
                    results.Add(info);
                }
            }

            return results;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool TryDescribe(
        Process process,
        IReadOnlyDictionary<int, string> commandLines,
        out SshProcessInfo info)
    {
        info = null!;
        try
        {
            var pid = process.Id;
            var startTime = process.StartTime;

            commandLines.TryGetValue(pid, out var commandLine);
            var targetHost = commandLine is null ? null : SshCommandLineParser.TryParseTargetHost(commandLine);

            info = new SshProcessInfo
            {
                ProcessId = pid,
                TargetHost = targetHost,
                StartTime = startTime,
            };
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Exited between GetProcessesByName and here, or this session can't see another
            // user's process (elevated ssh, different logon session). Not an error -- bs-8dr.
            return false;
        }
    }

    private static Dictionary<int, string> QueryCommandLines(ILogger logger)
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'ssh.exe'");
            using var results = searcher.Get();

            foreach (ManagementBaseObject result in results)
            {
                using (result)
                {
                    if (result["CommandLine"] is not string commandLine)
                    {
                        // No command line visible (permissions, or the process already exited
                        // between the WQL query and reading this row) -- skip rather than guess.
                        continue;
                    }

                    var pid = Convert.ToInt32(result["ProcessId"]);
                    map[pid] = commandLine;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            logger.LogWarning(
                ex,
                "CIM query for ssh.exe command lines failed; sessions will not correlate to a host this tick");
        }

        return map;
    }
}
