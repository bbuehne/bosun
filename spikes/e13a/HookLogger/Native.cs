using System.Globalization;
using System.Runtime.InteropServices;

namespace HookLogger;

/// <summary>
/// The only file in the spike logger that touches Win32. Everything here is read-only:
/// it observes processes and the caller's own token, and changes nothing.
/// </summary>
internal static class Native
{
    private const uint Th32csSnapProcess = 0x2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int TokenStatisticsClass = 10;
    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationClass = 20;
    private const uint TokenQuery = 0x0008;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatisticsData
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public uint TokenType;
        public uint ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // Leading fields of SECURITY_LOGON_SESSION_DATA; natural alignment matches the native layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityLogonSessionData
    {
        public uint Size;
        public Luid LogonId;
        public LsaUnicodeString UserName;
        public LsaUnicodeString LogonDomain;
        public LsaUnicodeString AuthenticationPackage;
        public uint LogonType;
        public uint Session;
        public IntPtr Sid;
        public long LogonTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] buffer, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("secur32.dll")]
    private static extern uint LsaGetLogonSessionData(ref Luid logonId, out IntPtr data);

    [DllImport("secur32.dll")]
    private static extern uint LsaFreeReturnBuffer(IntPtr buffer);

    internal readonly record struct ProcEntry(uint Pid, uint ParentPid, string ExeName);

    /// <param name="Error">Win32 error when the process could not be opened; null on success.</param>
    internal readonly record struct ProcInfo(string? ImagePath, long? CreationFileTime, bool? Alive, int? Error);

    internal sealed record OwnToken(
        string? LogonSessionId,
        string? LogonTimeUtc,
        uint? LogonType,
        uint? TerminalServicesSession,
        bool? Elevated,
        uint? ElevationType,
        string? Error);

    internal static Dictionary<uint, ProcEntry> Snapshot()
    {
        var result = new Dictionary<uint, ProcEntry>();
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
            {
                result[entry.ProcessId] = new ProcEntry(entry.ProcessId, entry.ParentProcessId, entry.ExeFile);
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return result;
    }

    internal static ProcInfo Query(uint pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
        {
            return new ProcInfo(null, null, null, Marshal.GetLastWin32Error());
        }

        try
        {
            long? created = GetProcessTimes(h, out long c, out _, out _, out _) ? c : null;
            bool? alive = GetExitCodeProcess(h, out uint code) ? code == StillActive : null;

            string? path = null;
            var buf = new char[2048];
            uint size = (uint)buf.Length;
            if (QueryFullProcessImageNameW(h, 0, buf, ref size))
            {
                path = new string(buf, 0, (int)size);
            }

            return new ProcInfo(path, created, alive, null);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>True when a process with this PID exists, is running, and has this creation time.</summary>
    internal static bool? IsAlive(uint pid, long creationFileTime)
    {
        ProcInfo info = Query(pid);
        if (info.Error is not null)
        {
            // Access denied (5) means it exists but cannot be inspected; anything else means gone.
            return info.Error == 5 ? null : false;
        }

        if (info.CreationFileTime != creationFileTime)
        {
            return false;
        }

        return info.Alive;
    }

    internal static OwnToken ReadOwnToken()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return new OwnToken(null, null, null, null, null, null, $"OpenProcessToken win32={Marshal.GetLastWin32Error()}");
            }

            string? luid = null;
            string? logonTime = null;
            uint? logonType = null;
            uint? tsSession = null;
            string? error = null;

            if (TryGet(token, TokenStatisticsClass, out TokenStatisticsData stats))
            {
                Luid id = stats.AuthenticationId;
                luid = FormatLuid(id);
                uint status = LsaGetLogonSessionData(ref id, out IntPtr data);
                if (status == 0 && data != IntPtr.Zero)
                {
                    try
                    {
                        var d = Marshal.PtrToStructure<SecurityLogonSessionData>(data);
                        logonTime = d.LogonTime > 0 ? FormatFileTime(d.LogonTime) : null;
                        logonType = d.LogonType;
                        tsSession = d.Session;
                    }
                    finally
                    {
                        LsaFreeReturnBuffer(data);
                    }
                }
                else
                {
                    error = $"LsaGetLogonSessionData ntstatus=0x{status:x}";
                }
            }
            else
            {
                error = $"TokenStatistics win32={Marshal.GetLastWin32Error()}";
            }

            bool? elevated = TryGet(token, TokenElevationClass, out uint isElevated) ? isElevated != 0 : null;
            uint? elevationType = TryGet(token, TokenElevationTypeClass, out uint type) ? type : null;

            return new OwnToken(luid, logonTime, logonType, tsSession, elevated, elevationType, error);
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    internal static string FormatFileTime(long fileTime) =>
        DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string FormatLuid(Luid l) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{l.High:x}:0x{l.Low:x}");

    private static bool TryGet<T>(IntPtr token, int infoClass, out T value)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            if (GetTokenInformation(token, infoClass, mem, size, out _))
            {
                value = Marshal.PtrToStructure<T>(mem);
                return true;
            }

            value = default;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }
}
