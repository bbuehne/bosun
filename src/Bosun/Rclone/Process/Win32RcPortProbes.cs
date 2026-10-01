using System.ComponentModel;
using System.Management;
using System.Security.Principal;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;
using Microsoft.Extensions.Logging;
using Diag = System.Diagnostics;

namespace Bosun.Rclone.Process;

/// <summary>
/// Real <see cref="IPortOwnerResolver"/>: reuses the <c>GetExtendedTcpTable</c> reader behind
/// <c>ISessionMonitor</c> (<see cref="ITcpConnectionReader"/>), so this adds no interop. IPv4
/// only, which is what <c>rclone rcd --rc-addr 127.0.0.1:port</c> binds.
/// </summary>
public sealed class TcpPortOwnerResolver(ITcpConnectionReader reader) : IPortOwnerResolver
{
    public int? GetListeningProcessId(int port)
    {
        foreach (var row in reader.GetConnections())
        {
            if (row.State != SessionSocketState.Listen || row.LocalEndPoint.Port != port)
            {
                continue;
            }

            // A listener on 127.0.0.1 or on any-address both stop us binding 127.0.0.1:port.
            // Anything on another interface does not.
            var address = row.LocalEndPoint.Address;
            if (System.Net.IPAddress.IsLoopback(address) || address.Equals(System.Net.IPAddress.Any))
            {
                return row.ProcessId;
            }
        }

        return null;
    }
}

/// <summary>
/// Real <see cref="IProcessInspector"/>: one CIM <c>Win32_Process</c> query for the name, image
/// path, command line and creation time, plus the <c>GetOwner</c> method for the account. This is
/// the same CIM route <c>CimSshProcessEnumerator</c> uses for <c>ssh.exe</c> command lines.
/// </summary>
/// <remarks>Query failures propagate: <see cref="RcPortGuard"/> treats an inspector that throws
/// as "unknown holder", which can never be killed. Only "no such process" returns null.</remarks>
public sealed class CimProcessInspector : IProcessInspector
{
    public ProcessDescription? Describe(int processId)
    {
        // Handle is Win32_Process's key property; without it in the SELECT the returned object has
        // no path and GetOwner below throws InvalidOperationException (found by the integration test).
        using var searcher = new ManagementObjectSearcher(
            $"SELECT Handle, ProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = {processId}");
        using var results = searcher.Get();

        foreach (ManagementBaseObject row in results)
        {
            using (row)
            {
                return new ProcessDescription
                {
                    ProcessId = processId,
                    Name = row["Name"] as string,
                    ImagePath = row["ExecutablePath"] as string,
                    CommandLine = row["CommandLine"] as string,
                    StartTime = row["CreationDate"] is string raw && raw.Length > 0
                        ? new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(raw))
                        : null,
                    Owner = row is ManagementObject instance ? TryGetOwner(instance) : null,
                };
            }
        }

        return null;
    }

    private static string? TryGetOwner(ManagementObject instance)
    {
        try
        {
            var args = new object?[2];
            var returnCode = instance.InvokeMethod("GetOwner", args);
            if (Convert.ToUInt32(returnCode) == 0 && args[0] is string user && args[1] is string domain)
            {
                return $"{domain}\\{user}";
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            // Unknown owner: the guard will not kill it.
        }

        return null;
    }
}

/// <summary>Real <see cref="IProcessTerminator"/> over <see cref="Diag.Process"/>.</summary>
public sealed class SystemProcessTerminator : IProcessTerminator
{
    // WMI's CreationDate and Process.StartTime come from the same kernel value but are rounded
    // differently, so compare with slack. A reused PID would differ by far more than this.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public bool TryKill(int processId, DateTimeOffset expectedStartTime)
    {
        try
        {
            using var process = Diag.Process.GetProcessById(processId);
            if (!SameInstance(process, expectedStartTime))
            {
                return false;
            }

            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public bool HasExited(int processId, DateTimeOffset expectedStartTime)
    {
        try
        {
            using var process = Diag.Process.GetProcessById(processId);
            return process.HasExited || !SameInstance(process, expectedStartTime);
        }
        catch (ArgumentException)
        {
            return true; // no such process
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SameInstance(Diag.Process process, DateTimeOffset expectedStartTime) =>
        (new DateTimeOffset(process.StartTime) - expectedStartTime).Duration() <= StartTimeTolerance;
}

/// <summary>Composition of the real guard for production wiring.</summary>
public static class RcPortGuardFactory
{
    public static RcPortGuard CreateForCurrentUser(
        RcloneProcessServiceOptions options, TimeProvider timeProvider, ILoggerFactory loggerFactory) =>
        new(
            new TcpPortOwnerResolver(new Win32TcpConnectionReader()),
            new CimProcessInspector(),
            new SystemProcessTerminator(),
            options,
            WindowsIdentity.GetCurrent().Name,
            timeProvider,
            loggerFactory.CreateLogger<RcPortGuard>());
}
