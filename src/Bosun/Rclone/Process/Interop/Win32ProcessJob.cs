using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bosun.Rclone.Process.Interop;

/// <summary>
/// The Job Object P/Invoke (ADR-020 §1, bs-772): <c>CreateJobObjectW</c>,
/// <c>SetInformationJobObject</c> with <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>, and
/// <c>AssignProcessToJobObject</c>. There is no managed .NET API for any of this.
/// </summary>
/// <remarks>
/// <para>
/// <b>Second interop file, deliberately.</b> docs/ARCHITECTURE.md §3 keeps raw interop behind
/// <c>ISessionMonitor</c> in <c>NativeTcpTable.cs</c>. ADR-020 §1 (accepted) requires a Job Object
/// and there is no way to get one without P/Invoke, so this is the one sanctioned exception. It
/// stays small and is reached only through <see cref="IProcessJob"/>; anything else needing
/// Win32 should still stop and ask.
/// </para>
/// <para>
/// <b>Lifetime.</b> One job per Bosun process. The handle is created lazily on the first
/// assignment and is never closed while Bosun runs: closing it is exactly what kills every
/// assigned child (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>). The production registration passes
/// an instance (not a factory) so the DI container does not dispose it. If Bosun dies, the OS
/// closes the handle and rcd dies with it, which is the point.
/// </para>
/// <para>
/// Bosun may itself already be inside a job (a debugger, a test runner, a shell). Since Windows 8
/// jobs nest, so assignment still succeeds; on older systems it would fail with access denied and
/// <see cref="TryAssign"/> reports that as a normal failure. The job does not set
/// <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>, so rcd's own children stay in the job too.
/// </para>
/// </remarks>
public sealed class Win32ProcessJob : IProcessJob, IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly object _gate = new();
    private SafeFileHandle? _job;
    private bool _disposed;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    public bool TryAssign(SafeProcessHandle process, out string error)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            SafeFileHandle job;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _job ??= CreateKillOnCloseJob();
                job = _job;
            }

            if (!AssignProcessToJobObject(job, process))
            {
                error = "AssignProcessToJobObject failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Closes the job handle, which makes the OS kill every assigned process. Production
    /// never calls this (the handle lives as long as Bosun); it exists so the opt-in integration
    /// test can prove the kill-on-close behaviour.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _job?.Dispose();
            _job = null;
        }
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW failed");
        }

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
            {
                var code = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(code, "SetInformationJobObject(KILL_ON_JOB_CLOSE) failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    // Layouts per winnt.h. Natural (default sequential) alignment gives 144 bytes on x64, which
    // Win32ProcessJobTests asserts: a wrong size would make SetInformationJobObject fail or,
    // worse, apply garbage flags.
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
