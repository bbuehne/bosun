using System.Buffers.Binary;
using System.Net;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;

namespace Bosun.Tests.SessionMonitor.Interop;

/// <summary>
/// Covers bs-8je's acceptance criterion directly: the <c>MIB_TCPTABLE_OWNER_PID</c> struct
/// layout and the port byte-swap are exercised against fixed byte patterns, never the real
/// <c>GetExtendedTcpTable</c> call (CLAUDE.md worktree-safety rules -- no default-suite test may
/// depend on what is actually running on this machine).
/// </summary>
/// <remarks>
/// <see cref="Win32TcpConnectionReader.ParseTcpTable"/> and
/// <see cref="Win32TcpConnectionReader.SwapPort"/> are <c>internal</c>, reachable here via
/// <c>InternalsVisibleTo("Bosun.Tests")</c> in <c>src/Bosun/AssemblyInfo.cs</c>.
/// </remarks>
public sealed class NativeTcpTableTests
{
    // MIB_TCP_STATE constants, straight from the Windows header -- see NativeTcpTable.MapState.
    private const uint MibTcpStateEstablished = 5;
    private const uint MibTcpStateTimeWait = 11;

    [Theory]
    [InlineData((ushort)443, 0xBB01u)] // 443 = 0x01BB: hi byte 0x01, lo byte 0xBB
    [InlineData((ushort)22, 0x1600u)] // 22 = 0x0016: hi byte 0x00, lo byte 0x16
    [InlineData((ushort)0, 0x0000u)]
    [InlineData((ushort)65535, 0xFFFFu)]
    [InlineData((ushort)8080, 0x901Fu)] // 8080 = 0x1F90: hi byte 0x1F, lo byte 0x90
    public void SwapPort_undoes_the_big_endian_packing_a_naive_little_endian_read_produces(
        ushort expectedPort, uint rawDwordLowWord)
    {
        Assert.Equal(expectedPort, Win32TcpConnectionReader.SwapPort(rawDwordLowWord));
    }

    [Fact]
    public void Parses_a_single_row_with_correct_field_offsets_and_port_swap()
    {
        var buffer = BuildTable(
            (MibTcpStateEstablished, new byte[] { 127, 0, 0, 1 }, (ushort)51000, new byte[] { 10, 0, 0, 5 }, (ushort)22, 4321));

        var rows = Win32TcpConnectionReader.ParseTcpTable(buffer);

        var row = Assert.Single(rows);
        Assert.Equal(4321, row.ProcessId);
        Assert.Equal(SessionSocketState.Established, row.State);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 51000), row.LocalEndPoint);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 22), row.RemoteEndPoint);
    }

    [Fact]
    public void Parses_multiple_rows_in_order()
    {
        var buffer = BuildTable(
            (MibTcpStateEstablished, new byte[] { 127, 0, 0, 1 }, (ushort)51000, new byte[] { 10, 0, 0, 5 }, (ushort)22, 100),
            (MibTcpStateTimeWait, new byte[] { 192, 168, 1, 1 }, (ushort)62000, new byte[] { 192, 168, 1, 2 }, (ushort)443, 200));

        var rows = Win32TcpConnectionReader.ParseTcpTable(buffer);

        Assert.Equal(2, rows.Count);
        Assert.Equal(100, rows[0].ProcessId);
        Assert.Equal(SessionSocketState.Established, rows[0].State);
        Assert.Equal(200, rows[1].ProcessId);
        Assert.Equal(SessionSocketState.TimeWait, rows[1].State);
        Assert.Equal(443, rows[1].RemoteEndPoint.Port);
    }

    [Fact]
    public void Zero_entries_yields_an_empty_list()
    {
        var buffer = BuildTable();

        Assert.Empty(Win32TcpConnectionReader.ParseTcpTable(buffer));
    }

    [Fact]
    public void Buffer_shorter_than_the_header_yields_an_empty_list_without_throwing()
    {
        Assert.Empty(Win32TcpConnectionReader.ParseTcpTable(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Truncated_buffer_that_claims_more_rows_than_it_holds_stops_cleanly_without_throwing()
    {
        // dwNumEntries says 2, but only one full row (24 bytes) follows the header. A corrupt or
        // truncated buffer must never crash the polling loop.
        var full = BuildTable(
            (MibTcpStateEstablished, new byte[] { 127, 0, 0, 1 }, (ushort)51000, new byte[] { 10, 0, 0, 5 }, (ushort)22, 100),
            (MibTcpStateEstablished, new byte[] { 127, 0, 0, 1 }, (ushort)51000, new byte[] { 10, 0, 0, 5 }, (ushort)22, 200));
        var truncated = full[..(4 + 24)]; // header + exactly one row's worth of bytes

        var rows = Win32TcpConnectionReader.ParseTcpTable(truncated);

        var row = Assert.Single(rows);
        Assert.Equal(100, row.ProcessId);
    }

    [Fact]
    public void Unrecognised_state_code_maps_to_unknown_rather_than_throwing()
    {
        var buffer = BuildTable(
            (99u, new byte[] { 127, 0, 0, 1 }, (ushort)1, new byte[] { 10, 0, 0, 1 }, (ushort)2, 1));

        var row = Assert.Single(Win32TcpConnectionReader.ParseTcpTable(buffer));
        Assert.Equal(SessionSocketState.Unknown, row.State);
    }

    /// <summary>
    /// Builds a raw <c>MIB_TCPTABLE_OWNER_PID</c> buffer byte-for-byte: a 4-byte
    /// <c>dwNumEntries</c> header, then one 24-byte row per entry (state, local addr, local
    /// port, remote addr, remote port, PID -- each a little-endian DWORD). Ports are packed the
    /// way the real API packs them: the port's two bytes in big-endian order, occupying the low
    /// word of the DWORD.
    /// </summary>
    private static byte[] BuildTable(
        params (uint state, byte[] localAddr, ushort localPort, byte[] remoteAddr, ushort remotePort, uint pid)[] rows)
    {
        var buffer = new byte[4 + (rows.Length * 24)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)rows.Length);

        var offset = 4;
        foreach (var (state, localAddr, localPort, remoteAddr, remotePort, pid) in rows)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), state);
            localAddr.CopyTo(buffer, offset + 4);
            WritePackedPort(buffer, offset + 8, localPort);
            remoteAddr.CopyTo(buffer, offset + 12);
            WritePackedPort(buffer, offset + 16, remotePort);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 20, 4), pid);
            offset += 24;
        }

        return buffer;
    }

    /// <summary>Writes a port into a DWORD's low word the way Windows does: big-endian within
    /// that word, i.e. memory byte 0 = high byte of the port, byte 1 = low byte, bytes 2-3
    /// unused/zero.</summary>
    private static void WritePackedPort(byte[] buffer, int offset, ushort port)
    {
        buffer[offset] = (byte)(port >> 8);
        buffer[offset + 1] = (byte)(port & 0xFF);
        buffer[offset + 2] = 0;
        buffer[offset + 3] = 0;
    }
}
