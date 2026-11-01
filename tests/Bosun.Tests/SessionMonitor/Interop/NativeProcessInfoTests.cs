using System.Diagnostics;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;

namespace Bosun.Tests.SessionMonitor.Interop;

/// <summary>
/// Pure conversions are asserted against fixed values. The last tests call the real Win32 reads,
/// and only for Bosun's own PID (or a PID that cannot exist): they open no other process, launch
/// nothing, and cannot reach a mount, drive letter or the Terminal fragment.
/// </summary>
public sealed class NativeProcessInfoTests
{
    [Fact]
    public void FileTime_of_the_unix_epoch_converts_exactly()
    {
        // 11644473600 s between 1601-01-01 and 1970-01-01, in 100 ns ticks.
        var result = NativeProcessInfo.FileTimeToUtc(116_444_736_000_000_000L);

        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
    }

    [Fact]
    public void FileTime_keeps_100ns_precision()
    {
        var result = NativeProcessInfo.FileTimeToUtc(116_444_736_000_000_000L + 1_234_567L);

        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567L), result);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void FileTime_that_is_zero_negative_or_out_of_range_is_unknown(long fileTime)
    {
        Assert.Null(NativeProcessInfo.FileTimeToUtc(fileTime));
    }

    [Theory]
    [InlineData(0u, 0, 0UL)]
    [InlineData(0x1234u, 0, 0x1234UL)]
    [InlineData(0xFFFFFFFFu, 0, 0xFFFFFFFFUL)]
    [InlineData(0x00000001u, 0x7, 0x0000000700000001UL)]
    [InlineData(0u, -1, 0xFFFFFFFF00000000UL)] // negative HighPart must not sign-extend into Low
    public void Luid_halves_fold_into_one_value(uint low, int high, ulong expected)
    {
        Assert.Equal(expected, NativeProcessInfo.LuidToUInt64(low, high));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", "WindowsTerminal.exe", true)]
    [InlineData(@"c:\x\windowsterminal.EXE", "WindowsTerminal.exe", true)]
    [InlineData(@"C:\x\WindowsTerminal.exe", "WindowsTerminal", true)]
    [InlineData(@"C:\x\explorer.exe", "WindowsTerminal.exe", false)] // PID recycled to something else
    [InlineData(null, "WindowsTerminal.exe", true)] // unknown path cannot contradict the name
    public void Image_path_check_catches_a_recycled_pid(string? path, string requested, bool expected)
    {
        Assert.Equal(expected, WindowsProcessSnapshotSource.ImagePathMatches(path, requested));
    }

    [Fact]
    public void A_pid_that_cannot_exist_yields_null_without_throwing()
    {
        Assert.Null(NativeProcessInfo.Read(0));
        Assert.Null(NativeProcessInfo.Read(-5));
        Assert.Null(NativeProcessInfo.Read(int.MaxValue)); // not a valid PID; OpenProcess refuses
    }

    /// <summary>
    /// REAL WIN32 CALLS, OWN PROCESS ONLY. Verifies the struct layouts and call sequence against
    /// the one process whose answer we know independently.
    /// </summary>
    [Fact]
    public void Reading_our_own_process_agrees_with_the_BCL_and_the_current_logon_session()
    {
        using var self = Process.GetCurrentProcess();
        var source = new WindowsProcessSnapshotSource();

        var facts = NativeProcessInfo.Read(Environment.ProcessId);

        Assert.NotNull(facts);
        Assert.False(string.IsNullOrWhiteSpace(facts.Value.ImagePath));
        Assert.Equal(self.MainModule!.FileName, facts.Value.ImagePath, ignoreCase: true);

        var created = facts.Value.CreationTimeUtc;
        Assert.NotNull(created);
        Assert.InRange(created.Value, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.True(ProcessIdentity.CreationTimesMatch(created.Value, self.StartTime.ToUniversalTime()));

        var session = facts.Value.LogonSession;
        Assert.NotNull(session);
        Assert.NotEqual(0UL, session.Value.Luid);
        Assert.NotNull(session.Value.LogonTimeUtc); // own session: the LSA lookup is permitted
        Assert.True(session.Value.LogonTimeUtc <= created.Value);
        Assert.Equal(source.CurrentLogonSession, session);
    }

    /// <summary>REAL ENUMERATION, but only our own PID is asserted on.</summary>
    [Fact]
    public void Snapshot_by_our_own_process_name_contains_our_own_entry()
    {
        using var self = Process.GetCurrentProcess();
        var source = new WindowsProcessSnapshotSource();

        var snapshot = source.Snapshot([self.ProcessName + ".exe"]);

        var mine = Assert.Single(snapshot, e => e.Pid == Environment.ProcessId);
        Assert.Equal(self.ProcessName + ".exe", mine.ImageName);
        Assert.NotNull(mine.ImagePath);
        Assert.Equal(source.CurrentLogonSession, mine.LogonSession);
    }
}
