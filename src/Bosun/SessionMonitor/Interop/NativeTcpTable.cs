using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace Bosun.SessionMonitor.Interop;

/// <summary>
/// <c>GetExtendedTcpTable</c> P/Invoke (bs-8je). This is the ONLY file in the project that
/// declares a <c>DllImport</c> -- docs/ARCHITECTURE.md §3 requires every bit of raw interop to
/// stay behind <c>ISessionMonitor</c> and confined to one file; if a second <c>DllImport</c>
/// shows up anywhere else, that's an architecture violation, not a style nit.
/// </summary>
/// <remarks>
/// Two things reliably go wrong here, both handled deliberately (bs-8je):
/// <list type="bullet">
/// <item>The buffer-sizing dance: call with a null buffer to learn the size, allocate, call
/// again -- and the size can change between the two calls (another connection opened
/// concurrently), so <see cref="Win32TcpConnectionReader.GetConnections"/> loops rather than
/// assuming the second call succeeds.</item>
/// <item>Ports in <c>MIB_TCPROW_OWNER_PID</c> occupy the low 16 bits of a DWORD in network
/// (big-endian) byte order; <see cref="SwapPort"/> undoes that. IPv4 addresses are NOT
/// byte-swapped -- they already arrive in the byte order <see cref="IPEndPoint"/> expects.</item>
/// </list>
/// <see cref="ParseTcpTable"/> and <see cref="SwapPort"/> are pure functions over raw bytes --
/// exactly the buffer shape the real API returns -- so the struct layout and the byte-swap are
/// unit-tested against fixed byte patterns without ever calling the real API.
/// <c>InternalsVisibleTo("Bosun.Tests")</c> in <c>src/Bosun/AssemblyInfo.cs</c> is what makes
/// that possible without widening the public surface.
/// </remarks>
public interface ITcpConnectionReader
{
    IReadOnlyList<TcpConnectionInfo> GetConnections();
}

/// <summary>One row from the TCP table: an owning process and its connection.</summary>
public sealed record TcpConnectionInfo
{
    public required int ProcessId { get; init; }
    public required SessionSocketState State { get; init; }
    public required IPEndPoint LocalEndPoint { get; init; }
    public required IPEndPoint RemoteEndPoint { get; init; }
}

/// <summary>
/// Real <see cref="ITcpConnectionReader"/> (bs-8je): wraps <c>GetExtendedTcpTable</c> from
/// <c>iphlpapi.dll</c> with <c>TCP_TABLE_OWNER_PID_ALL</c>. Production only -- exercising the
/// real syscall is a marked integration test, never part of the default suite (CLAUDE.md
/// worktree-safety rules).
/// </summary>
public sealed class Win32TcpConnectionReader : ITcpConnectionReader
{
    private const int AfInet = 2; // AF_INET
    private const int TcpTableOwnerPidAll = 5; // TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaxAttempts = 5;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        int reserved);

    public IReadOnlyList<TcpConnectionInfo> GetConnections()
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (size <= 0)
            {
                return [];
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var requestedSize = size;
                var result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0);

                if (result == NoError)
                {
                    var bytes = new byte[requestedSize];
                    Marshal.Copy(buffer, bytes, 0, requestedSize);
                    return ParseTcpTable(bytes);
                }

                if (result == ErrorInsufficientBuffer)
                {
                    // Size grew between the two calls (a connection opened concurrently) --
                    // `size` now holds the new required size. Loop and try again rather than
                    // assuming the first answer still holds.
                    continue;
                }

                throw new Win32Exception(unchecked((int)result));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            $"GetExtendedTcpTable did not stabilize after {MaxAttempts} attempts.");
    }

    /// <summary>
    /// Parses the raw <c>MIB_TCPTABLE_OWNER_PID</c> buffer: a 4-byte <c>dwNumEntries</c> header
    /// followed by that many 24-byte <c>MIB_TCPROW_OWNER_PID</c> rows (state, local address,
    /// local port, remote address, remote port, owning PID -- each a little-endian DWORD). Pure
    /// and side-effect-free so it can be exercised with fixed byte patterns in tests instead of
    /// the real API.
    /// </summary>
    internal static List<TcpConnectionInfo> ParseTcpTable(ReadOnlySpan<byte> buffer)
    {
        const int HeaderSize = 4;
        const int RowSize = 24;

        var rows = new List<TcpConnectionInfo>();
        if (buffer.Length < HeaderSize)
        {
            return rows;
        }

        var numEntries = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..HeaderSize]);
        var offset = HeaderSize;

        for (var i = 0; i < numEntries; i++)
        {
            if (offset + RowSize > buffer.Length)
            {
                // The buffer is shorter than dwNumEntries claims -- stop rather than read past
                // the end. Should never happen against the real API, but a truncated/corrupt
                // buffer must not crash the polling loop.
                break;
            }

            var row = buffer.Slice(offset, RowSize);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row[..4]);
            var localAddr = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4, 4));
            var localPortRaw = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4));
            var remoteAddr = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(12, 4));
            var remotePortRaw = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            var owningPid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4));

            rows.Add(new TcpConnectionInfo
            {
                ProcessId = unchecked((int)owningPid),
                State = MapState(state),
                LocalEndPoint = new IPEndPoint(localAddr, SwapPort(localPortRaw)),
                RemoteEndPoint = new IPEndPoint(remoteAddr, SwapPort(remotePortRaw)),
            });

            offset += RowSize;
        }

        return rows;
    }

    /// <summary>
    /// Ports in <c>MIB_TCPROW_OWNER_PID</c> occupy the low 16 bits of a DWORD, in network
    /// (big-endian) byte order. A naive little-endian read of that DWORD leaves the two port
    /// bytes swapped relative to each other; this undoes exactly that swap. The upper 16 bits of
    /// the DWORD are always zero and are ignored.
    /// </summary>
    internal static ushort SwapPort(uint rawPort) =>
        (ushort)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));

    private static SessionSocketState MapState(uint state) => state switch
    {
        1 => SessionSocketState.Closed,
        2 => SessionSocketState.Listen,
        3 => SessionSocketState.SynSent,
        4 => SessionSocketState.SynReceived,
        5 => SessionSocketState.Established,
        6 => SessionSocketState.FinWait1,
        7 => SessionSocketState.FinWait2,
        8 => SessionSocketState.CloseWait,
        9 => SessionSocketState.Closing,
        10 => SessionSocketState.LastAck,
        11 => SessionSocketState.TimeWait,
        12 => SessionSocketState.DeleteTcb,
        _ => SessionSocketState.Unknown,
    };
}
