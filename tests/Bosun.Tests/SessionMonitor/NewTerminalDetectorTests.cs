using Bosun.SessionMonitor;

namespace Bosun.Tests.SessionMonitor;

/// <summary>
/// The pure differ behind ADR-021 Decision 7. Snapshots are hand-built; nothing here reads a real
/// process.
/// </summary>
public sealed class NewTerminalDetectorTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshotEntry Entry(
        int pid, DateTime created, string image = ClaudeProcessImages.WindowsTerminal) =>
        new() { Pid = pid, CreationTimeUtc = created, ImageName = image };

    [Fact]
    public void A_terminal_absent_from_the_previous_snapshot_is_reported()
    {
        var added = Entry(200, T0.AddMinutes(5));

        var result = NewTerminalDetector.FindNewTerminals(
            [Entry(100, T0)], [Entry(100, T0), added]);

        Assert.Equal([added], result);
    }

    [Fact]
    public void The_same_pid_with_a_different_creation_time_counts_as_new()
    {
        // PID reuse: Terminal exited and a new Terminal got the same PID between two ticks.
        var reused = Entry(100, T0.AddHours(3));

        var result = NewTerminalDetector.FindNewTerminals([Entry(100, T0)], [reused]);

        Assert.Equal([reused], result);
    }

    [Fact]
    public void An_unchanged_terminal_is_not_reported()
    {
        var result = NewTerminalDetector.FindNewTerminals([Entry(100, T0)], [Entry(100, T0)]);

        Assert.Empty(result);
    }

    [Fact]
    public void A_terminal_that_exited_is_not_reported()
    {
        var result = NewTerminalDetector.FindNewTerminals([Entry(100, T0), Entry(101, T0)], [Entry(101, T0)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Creation_times_within_one_millisecond_are_the_same_process()
    {
        // The comparison tolerance of ADR-021 Decision 5 applies here too: a differently-rounded
        // read of the same process must not look like a new Terminal.
        var rounded = Entry(100, T0.AddTicks(7_000));

        var result = NewTerminalDetector.FindNewTerminals([Entry(100, T0)], [rounded]);

        Assert.Empty(result);
    }

    [Fact]
    public void Creation_times_more_than_a_millisecond_apart_are_different_processes()
    {
        var later = Entry(100, T0.AddTicks(ProcessIdentity.CreationTimeTolerance.Ticks + 1));

        var result = NewTerminalDetector.FindNewTerminals([Entry(100, T0)], [later]);

        Assert.Equal([later], result);
    }

    [Fact]
    public void Only_terminal_processes_are_considered()
    {
        var result = NewTerminalDetector.FindNewTerminals(
            [], [Entry(300, T0, ClaudeProcessImages.Claude), Entry(301, T0, ClaudeProcessImages.Node)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Image_name_matching_ignores_case()
    {
        var terminal = Entry(100, T0, "windowsterminal.EXE");

        Assert.Equal([terminal], NewTerminalDetector.FindNewTerminals([], [terminal]));
    }

    [Fact]
    public void Against_an_empty_baseline_every_running_terminal_is_new()
    {
        var a = Entry(100, T0);
        var b = Entry(101, T0.AddMinutes(1));

        Assert.Equal([a, b], NewTerminalDetector.FindNewTerminals([], [a, b]));
    }

    [Fact]
    public void A_non_terminal_with_a_terminals_pid_in_the_previous_snapshot_does_not_mask_a_new_terminal()
    {
        var terminal = Entry(100, T0);

        var result = NewTerminalDetector.FindNewTerminals(
            [Entry(100, T0, ClaudeProcessImages.Node)], [terminal]);

        Assert.Equal([terminal], result);
    }
}
