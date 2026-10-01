using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.Supervisor.Independent.RcTimeout;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// ADR-020 §2 (bs-772): a held rc port is cleared only when the holder is provably Bosun's own
/// stale rcd; anything else is reported and left alone. Every collaborator is a fake, so nothing
/// here can kill, inspect or bind anything real.
/// </summary>
public sealed class RcPortGuardTests
{
    private const int Port = 5572;
    private const string Config = @"C:\Users\Barry\AppData\Roaming\rclone\rclone.conf";
    private const string User = @"DESKTOP\Barry";
    private const int StalePid = 31032;

    [Fact]
    public async Task A_free_port_launches_without_inspecting_or_killing_anything()
    {
        var h = new Harness();

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Equal(RcPortCheckOutcome.Free, result.Outcome);
        Assert.Empty(h.Terminator.KillCalls);
    }

    [Fact]
    public async Task A_stale_rcd_of_Bosuns_own_is_killed_and_the_launch_may_proceed()
    {
        var h = new Harness();
        var stale = StaleRcdMatcherTests.Matching(StalePid);
        h.Resolver.Enqueue(StalePid);   // before the kill: held
        h.Resolver.Enqueue(null);       // after the kill: free
        h.Inspector.Set(stale, StalePid);

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Equal(RcPortCheckOutcome.StaleRcdKilled, result.Outcome);
        var kill = Assert.Single(h.Terminator.KillCalls);
        Assert.Equal(StalePid, kill.Pid);
        Assert.Equal(stale.StartTime, kill.Start); // PID-reuse guard: the inspected start time travels with the kill
    }

    [Fact]
    public async Task Killing_a_stale_rcd_is_logged_at_Warning_with_its_pid_and_start_time()
    {
        var h = new Harness();
        var stale = StaleRcdMatcherTests.Matching(StalePid);
        h.Resolver.Enqueue(StalePid);
        h.Resolver.Enqueue(null);
        h.Inspector.Set(stale, StalePid);

        await h.Guard.EnsureFreeAsync(CancellationToken.None);

        var warning = Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(StalePid.ToString(), warning.Message);
        Assert.Contains("2026-09-16", warning.Message); // start time
    }

    [Fact]
    public async Task The_launch_waits_for_the_killed_process_to_actually_exit()
    {
        var h = new Harness();
        var stale = StaleRcdMatcherTests.Matching(StalePid);
        h.Resolver.Enqueue(StalePid);
        h.Resolver.Default = null;
        h.Inspector.Set(stale, StalePid);
        h.Terminator.ExitsAfterKill = false;

        var task = h.Guard.EnsureFreeAsync(CancellationToken.None);

        // Still not exited: the guard must be waiting on the injected clock, not returning.
        await AdvanceUntilCompleteAsync(task, h.Time, TimeSpan.FromMilliseconds(100), maxAdvance: TimeSpan.FromSeconds(2));
        Assert.False(task.IsCompleted);

        // Now it exits; the next poll sees it.
        h.Terminator.ExitsAfterKill = true;
        await AdvanceUntilCompleteAsync(task, h.Time, TimeSpan.FromMilliseconds(100), maxAdvance: TimeSpan.FromSeconds(5));
        Assert.Equal(RcPortCheckOutcome.StaleRcdKilled, (await task.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    [Fact]
    public async Task A_stale_rcd_that_will_not_exit_is_reported_not_launched_over()
    {
        var h = new Harness(options => options with { StaleKillTimeout = TimeSpan.FromSeconds(1) });
        h.Resolver.Default = StalePid;
        h.Inspector.Set(StaleRcdMatcherTests.Matching(StalePid), StalePid);
        h.Terminator.ExitsAfterKill = false;

        var task = h.Guard.EnsureFreeAsync(CancellationToken.None);
        await AdvanceUntilCompleteAsync(task, h.Time, TimeSpan.FromMilliseconds(100), maxAdvance: TimeSpan.FromSeconds(10));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RcPortCheckOutcome.HeldByOtherProcess, result.Outcome);
        Assert.Contains(StalePid.ToString(), result.Message);
    }

    [Fact]
    public async Task A_stale_rcd_that_cannot_be_terminated_is_reported_not_launched_over()
    {
        var h = new Harness();
        h.Resolver.Default = StalePid;
        h.Inspector.Set(StaleRcdMatcherTests.Matching(StalePid), StalePid);
        h.Terminator.KillSucceeds = false;

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Equal(RcPortCheckOutcome.HeldByOtherProcess, result.Outcome);
        Assert.Contains("could not be terminated", result.Message);
    }

    [Fact]
    public async Task Port_still_held_after_the_kill_is_reported_with_the_new_holder()
    {
        var h = new Harness();
        h.Resolver.Enqueue(StalePid);
        h.Resolver.Enqueue(777); // someone else grabbed it
        h.Inspector.Set(StaleRcdMatcherTests.Matching(StalePid), StalePid);
        h.Inspector.Set(
            new ProcessDescription { ProcessId = 777, Name = "other.exe", ImagePath = @"C:\other\other.exe" }, 777);

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Equal(RcPortCheckOutcome.HeldByOtherProcess, result.Outcome);
        Assert.Contains("PID 777", result.Message);
    }

    // -- never kill anything that does not match exactly ---------------------------------------

    public static TheoryData<string, ProcessDescription> NonMatchingHolders()
    {
        var baseline = StaleRcdMatcherTests.Matching(4242);
        return new TheoryData<string, ProcessDescription>
        {
            { "different port", baseline with { CommandLine = baseline.CommandLine!.Replace("127.0.0.1:5572", "127.0.0.1:5999") } },
            { "different config", baseline with { CommandLine = baseline.CommandLine!.Replace("rclone.conf", "other.conf") } },
            { "different user", baseline with { Owner = @"DESKTOP\Mallory" } },
            { "not rclone", baseline with { Name = "node.exe", ImagePath = @"C:\node\node.exe" } },
            { "no name", baseline with { Name = null } },
            { "no image path", baseline with { ImagePath = null } },
            { "no command line", baseline with { CommandLine = null } },
            { "no owner", baseline with { Owner = null } },
            { "no start time", baseline with { StartTime = null } },
            { "extra flag", baseline with { CommandLine = baseline.CommandLine + " --rc-no-auth" } },
        };
    }

    [Theory]
    [MemberData(nameof(NonMatchingHolders))]
    public async Task A_holder_that_does_not_match_exactly_is_never_killed_and_is_reported(
        string why, ProcessDescription holder)
    {
        var h = new Harness();
        h.Resolver.Default = holder.ProcessId;
        h.Inspector.Set(holder, holder.ProcessId);

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.True(h.Terminator.KillCalls.Count == 0, $"killed a non-matching holder ({why})");
        Assert.Equal(RcPortCheckOutcome.HeldByOtherProcess, result.Outcome);
        Assert.Contains($"PID {holder.ProcessId}", result.Message);
        Assert.Contains(holder.ImagePath ?? holder.Name ?? "image path unavailable", result.Message);
    }

    [Fact]
    public async Task A_holder_that_cannot_be_inspected_is_never_killed()
    {
        var h = new Harness();
        h.Resolver.Default = 4242;
        h.Inspector.SetThrows(4242, new InvalidOperationException("access denied (fake)"));

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Empty(h.Terminator.KillCalls);
        Assert.Equal(RcPortCheckOutcome.HeldByOtherProcess, result.Outcome);
        Assert.Contains("PID 4242", result.Message);
    }

    [Fact]
    public async Task A_holder_that_vanished_before_it_could_be_inspected_is_treated_as_free()
    {
        var h = new Harness();
        h.Resolver.Default = 4242;
        h.Inspector.Set(null, 4242);

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Equal(RcPortCheckOutcome.Free, result.Outcome);
        Assert.Empty(h.Terminator.KillCalls);
    }

    [Fact]
    public async Task If_the_owner_lookup_itself_fails_the_launch_proceeds()
    {
        var h = new Harness();
        h.Resolver.EnqueueThrow(new InvalidOperationException("tcp table unavailable (fake)"));

        var result = await h.Guard.EnsureFreeAsync(CancellationToken.None);

        Assert.Equal(RcPortCheckOutcome.Free, result.Outcome);
        Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void DescribeHolder_names_pid_and_image_path_and_is_null_when_free()
    {
        var h = new Harness();
        Assert.Null(h.Guard.DescribeHolder());

        h.Resolver.Default = 4242;
        h.Inspector.Set(
            new ProcessDescription { ProcessId = 4242, Name = "rclone.exe", ImagePath = @"C:\x\rclone.exe" }, 4242);

        var text = h.Guard.DescribeHolder();
        Assert.Equal(@"port 5572 is held by PID 4242 (C:\x\rclone.exe)", text);
    }

    /// <summary>Same shape as <c>RcloneProcessServiceTests.AdvanceUntilCompleteAsync</c>: offers the
    /// injected clock repeatedly (bounded) until the task finishes. The brief real-time yield is
    /// harness synchronisation for the thread-pool hop after a timer fires, not simulated timing.</summary>
    private static async Task AdvanceUntilCompleteAsync(Task task, FakeTimeProvider time, TimeSpan step, TimeSpan maxAdvance)
    {
        var advanced = TimeSpan.Zero;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!task.IsCompleted && advanced < maxAdvance && DateTime.UtcNow < deadline)
        {
            time.Advance(step);
            advanced += step;
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private sealed class Harness
    {
        public Harness(Func<RcloneProcessServiceOptions, RcloneProcessServiceOptions>? configure = null)
        {
            var options = new RcloneProcessServiceOptions { RcloneRcPort = Port, RcloneConfigPath = Config };
            Guard = new RcPortGuard(
                Resolver, Inspector, Terminator, configure?.Invoke(options) ?? options, User, Time, Logger);
        }

        public FakePortOwnerResolver Resolver { get; } = new();
        public FakeProcessInspector Inspector { get; } = new();
        public FakeProcessTerminator Terminator { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public LevelRecordingLogger<RcPortGuard> Logger { get; } = new();
        public RcPortGuard Guard { get; }
    }
}
