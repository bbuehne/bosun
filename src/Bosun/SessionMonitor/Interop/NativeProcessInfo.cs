using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bosun.SessionMonitor.Interop;

/// <summary>What <see cref="NativeProcessInfo.Read"/> could learn about one process. Each field is
/// independently <see langword="null"/> when that particular call was refused.</summary>
internal readonly record struct NativeProcessFacts(
    DateTime? CreationTimeUtc, string? ImagePath, LogonSessionId? LogonSession);

/// <summary>
/// Per-process Win32 reads for the Claude Code process snapshot (ADR-021 Decisions 4 and 5,
/// bs-vqh.7): image path, creation time, and logon session. This is the second sanctioned
/// P/Invoke file under <c>SessionMonitor/Interop/</c> (the first is <see cref="Win32TcpConnectionReader"/>);
/// ADR-021's Consequences place the token calls here. Nothing else in <c>Bosun</c> gains P/Invoke.
/// </summary>
/// <remarks>
/// <para>
/// Access model: one <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> serves
/// <c>QueryFullProcessImageName</c> and <c>GetProcessTimes</c>. That right is granted even for
/// elevated and other-user processes (not for protected ones such as System/Registry/lsass-PPL),
/// so those usually still yield a path and creation time. <c>OpenProcessToken(TOKEN_QUERY)</c> is
/// the call a medium-integrity Bosun can be refused on an elevated process; that, and
/// <c>LsaGetLogonSessionData</c> being refused for another user's session, leave the corresponding
/// fields <see langword="null"/>. No failure here throws: "unknown" is an ordinary answer, and
/// ADR-021 Decision 4 relies on it (an elevated Claude Code process is never tracked anyway).
/// </para>
/// <para>
/// Creation time keeps full FILETIME precision (100 ns). The 1 ms comparison tolerance of
/// ADR-021 Decision 5 is the consumer's job (<see cref="ProcessIdentity"/>).
/// </para>
/// <para>
/// Exercising the real calls against arbitrary processes is not part of the default suite. The
/// pure pieces (<see cref="FileTimeToUtc"/>, <see cref="LuidToUInt64"/>) are unit-tested; the
/// default suite additionally reads Bosun's own process only, which cannot affect any mount.
/// </para>
/// </remarks>
internal static class NativeProcessInfo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenStatisticsClass = 10; // TOKEN_INFORMATION_CLASS.TokenStatistics
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxPathChars = 32768;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // TOKEN_STATISTICS, full layout: GetTokenInformation needs a buffer at least this large.
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
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

    // Leading part of SECURITY_LOGON_SESSION_DATA, up to and including LogonTime. The marshaller
    // computes the offsets (x64: LogonTime at 80), so none are hand-counted. Reading a prefix is
    // safe because the structure is allocated by the LSA at its full size.
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityLogonSessionDataPrefix
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeFileHandle process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeFileHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeFileHandle process, uint desiredAccess, out SafeFileHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeFileHandle token, int informationClass, IntPtr information, uint informationLength, out uint returnLength);

    // NTSTATUS return; the LSA calls do not set the Win32 last error.
    [DllImport("secur32.dll")]
    private static extern uint LsaGetLogonSessionData(ref Luid logonId, out IntPtr sessionData);

    [DllImport("secur32.dll")]
    private static extern uint LsaFreeReturnBuffer(IntPtr buffer);

    /// <summary>
    /// Reads image path, creation time and logon session for <paramref name="pid"/> through a
    /// single process handle. Returns <see langword="null"/> only when the process cannot be
    /// opened at all (gone, protected, or access denied); otherwise returns whatever fields could
    /// be read. Never throws for an OS refusal.
    /// </summary>
    public static NativeProcessFacts? Read(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            return null;
        }

        return new NativeProcessFacts(
            ReadCreationTimeUtc(process),
            ReadImagePath(process),
            ReadLogonSession(process));
    }

    /// <summary>Logon session of the current process, or <see langword="null"/> if unreadable.</summary>
    public static LogonSessionId? ReadCurrentLogonSession() => Read(Environment.ProcessId)?.LogonSession;

    /// <summary>
    /// FILETIME (100 ns ticks since 1601-01-01 UTC) to a UTC <see cref="DateTime"/> with no loss of
    /// precision. <see langword="null"/> for zero, negative or out-of-range values (a zero
    /// creation time means the call produced nothing, and must not become year 1601).
    /// </summary>
    internal static DateTime? FileTimeToUtc(long fileTime)
    {
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return DateTime.FromFileTimeUtc(fileTime);
    }

    /// <summary>Folds a LUID's two halves into one 64-bit value, <c>HighPart</c> on top.</summary>
    internal static ulong LuidToUInt64(uint lowPart, int highPart) =>
        ((ulong)(uint)highPart << 32) | lowPart;

    private static DateTime? ReadCreationTimeUtc(SafeFileHandle process) =>
        GetProcessTimes(process, out var creation, out _, out _, out _)
            ? FileTimeToUtc(creation)
            : null;

    private static string? ReadImagePath(SafeFileHandle process)
    {
        var capacity = 1024u;
        while (capacity <= MaxPathChars)
        {
            var buffer = new char[capacity];
            var size = capacity; // in: capacity in chars; out: length written, excluding the NUL
            if (QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                return new string(buffer, 0, (int)size);
            }

            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                return null;
            }

            capacity *= 2;
        }

        return null;
    }

    private static LogonSessionId? ReadLogonSession(SafeFileHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null; // access denied for an elevated / other-user process: "unknown"
        }

        using (token)
        {
            var size = Marshal.SizeOf<TokenStatistics>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenStatisticsClass, buffer, (uint)size, out _))
                {
                    return null;
                }

                var stats = Marshal.PtrToStructure<TokenStatistics>(buffer);
                var luid = stats.AuthenticationId;
                return new LogonSessionId(
                    LuidToUInt64(luid.LowPart, luid.HighPart),
                    ReadLogonTimeUtc(luid));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static DateTime? ReadLogonTimeUtc(Luid luid)
    {
        if (LsaGetLogonSessionData(ref luid, out var data) != 0 || data == IntPtr.Zero)
        {
            return null; // typically STATUS_ACCESS_DENIED for another user's session
        }

        try
        {
            var prefix = Marshal.PtrToStructure<SecurityLogonSessionDataPrefix>(data);
            return prefix.Size >= Marshal.SizeOf<SecurityLogonSessionDataPrefix>()
                ? FileTimeToUtc(prefix.LogonTime)
                : null;
        }
        finally
        {
            _ = LsaFreeReturnBuffer(data);
        }
    }
}
