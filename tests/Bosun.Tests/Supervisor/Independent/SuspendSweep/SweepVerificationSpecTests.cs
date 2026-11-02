using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweep;

/// <summary>
/// bs-5o4, the verification and escalation phases, against Invariant I2 and rule 4: "a host the table no
/// longer lists completes its drain (rule 4: the unmount call's own result is never proof). A host still
/// listed whose budget has run out is escalated ... A failed or hung request on one host does not delay or
/// stop the others." Written from docs/ARCHITECTURE.md §4 rules 4 and 5, not from the implementation.
/// </summary>
/// <remarks>
/// A forced unmount is observed the way <c>SuspendBudgetHarness</c> observes it: the warning-level log line
/// that names the host and contains "escalat" (<see cref="IRcloneClient"/> has no separate force endpoint).
/// </remarks>
public sealed class SweepVerificationSpecTests
{
    private static async Task<PreemptSpecHarness> MountedAsync()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"), Persistent("charlie", "O:"));
        await h.StartAsync();
        return h;
    }

    private static RcloneRcTimeoutException Timeout() => new("mount/unmount", TimeSpan.FromSeconds(30));

    /// <summary>
    /// <b>Protects:</b> I2 / rule 4, "the unmount call's own result is never proof".
    /// <b>Catches:</b> a sweep that completes a host's drain because its <c>mount/unmount</c> returned without
    /// error, while rclone still lists the mount: the state machine believes a drive is gone that is not.
    /// </summary>
    [Fact]
    public async Task An_unmount_that_returns_but_leaves_the_drive_listed_keeps_the_host_Draining()
    {
        var h = await MountedAsync();
        h.Rclone.ConfirmUnmountAtCall("N:", int.MaxValue);

        await h.SuspendAsync();

        Assert.True(h.IsMountedInRclone("N:"), "setup: bravo's drive must still be listed");
        Assert.Equal(MountState.Draining, h.State("bravo"));
        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.Equal(MountState.Disabled, h.State("charlie"));
    }

    /// <summary>
    /// <b>Protects:</b> I2 when the call itself fails (the 30 s <c>RcloneRcTimeoutException</c>): the host is
    /// never believed gone and never left <c>Mounted</c>; its forced unmount comes once, at arrival + budget;
    /// and while rclone keeps listing the drive the host stays <c>Draining</c>, however long that is.
    /// <b>Catches:</b> a failed request treated as "done" (Disabled) or as "abort the drain" (back to Mounted,
    /// or a Draining host dropped from the retry path); a second escalation for an already-escalated host;
    /// the failure of one host's call faulting the whole sweep (the others not completed).
    /// </summary>
    [Fact]
    public async Task A_failed_unmount_on_one_host_escalates_once_at_the_budget_and_is_never_believed_gone()
    {
        var h = await MountedAsync();
        h.Rclone.ConfirmUnmountAtCall("N:", int.MaxValue); // bravo's drive never goes away
        var failing = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "N:");
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(failing, "bravo's unmount");
        failing.Fail(Timeout());
        await h.FinishAsync(pump, "the sweep never finished after bravo's call failed", suspend);

        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.Equal(MountState.Disabled, h.State("charlie"));
        Assert.Equal(MountState.Draining, h.State("bravo"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 1), Step);
        var escalations = h.EscalationTimes("bravo");
        Assert.True(escalations.Count == 1, $"expected one escalation for bravo, saw {escalations.Count}");
        Assert.InRange(escalations[0], arrival.AddSeconds(Budget) - Step, arrival.AddSeconds(Budget) + Step);
        Assert.Equal(1, UnmountsAt(h, "N:", arrival));

        await h.AdvanceAsync(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));
        Assert.Equal(MountState.Draining, h.State("bravo"));
        Assert.DoesNotContain(h.Transitions("bravo"), t => t.From == MountState.Draining && t.To != MountState.Disabled);
        Assert.Single(h.EscalationTimes("bravo"));
        Assert.Empty(h.EscalationTimes("alpha"));
        Assert.Empty(h.EscalationTimes("charlie"));
    }

    /// <summary>
    /// <b>Protects:</b> the other half of "the call's result is never proof": the table is. A call that timed
    /// out over HTTP while rclone did unmount the drive completes its drain on the verification.
    /// <b>Catches:</b> a failed call treated as "still mounted" without consulting <c>mount/listmounts</c>:
    /// a pointless forced unmount, and a host left Draining that is in fact gone.
    /// </summary>
    [Fact]
    public async Task An_unmount_call_that_fails_while_the_drive_is_actually_gone_completes_on_verification()
    {
        var h = await MountedAsync();
        var failing = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "N:");

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(failing, "bravo's unmount");
        h.Rclone.DropMountSilently("N:");
        failing.Fail(Timeout());
        await h.FinishAsync(pump, "the sweep never finished", suspend);

        Assert.Equal(MountState.Disabled, h.State("bravo"));
        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 2), Step);
        Assert.Empty(h.EscalationTimes("bravo"));
    }

    /// <summary>
    /// <b>Protects:</b> I2 when the verification itself cannot be done: rclone rcd does not answer
    /// <c>mount/listmounts</c>. The requests still all go out at arrival (they come first), but nobody's drain
    /// completes, and each host is escalated exactly once.
    /// <b>Catches:</b> a failed listmounts read as "nothing is listed" (every host believed gone); a failed
    /// listmounts that aborts the sweep before the requests go out; repeated escalations per retry.
    /// </summary>
    [Fact]
    public async Task When_listmounts_fails_every_request_still_goes_out_and_no_host_is_believed_gone()
    {
        var h = await MountedAsync();
        h.Rclone.ListMountsAlwaysFails = true;
        var arrival = h.Now;

        await h.SuspendAsync();

        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(1, UnmountsAt(h, "N:", arrival));
        Assert.Equal(1, UnmountsAt(h, "O:", arrival));

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));
        foreach (var key in new[] { "alpha", "bravo", "charlie" })
        {
            Assert.Equal(MountState.Draining, h.State(key));
            Assert.Single(h.EscalationTimes(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> rule 5 as amended 2026-11-02 after this test first failed: a hung request on one host
    /// does NOT hold back any other host's FIRST request (all leave at arrival, I8), but the sweep's
    /// verification and forced unmounts wait for the slowest request. alpha's <c>mount/unmount</c> hangs for its
    /// whole 30 s timeout; bravo's returns at once but does not take effect.
    /// <b>Catches:</b> bravo's first request held behind alpha's (a serial sweep); bravo never escalated once
    /// alpha's call returns, or escalated twice.
    /// </summary>
    /// <remarks>
    /// Originally written to require bravo's forced unmount by arrival + budget; that failed at c0d4a7c and the
    /// spec was amended to accept the delay (ARCHITECTURE §4 rule 5; note after the bs-5o4 amendment in
    /// DECISIONS.md): a second identical request at 3 s or 30 s both land after Windows has slept.
    /// The clock is moved in 10 ms steps. The channel is pumped again only when the previous pump has
    /// finished, so there is never a second consumer.
    /// </remarks>
    [Fact]
    public async Task One_hung_unmount_never_holds_another_hosts_first_request_and_its_forced_unmount_follows_the_slowest_call()
    {
        var h = await MountedAsync();
        h.Rclone.ConfirmUnmountAtCall("N:", 2); // bravo: the first request does nothing; the forced one works
        var hung = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hung, "alpha's unmount");

        var until = arrival.AddSeconds(30);
        while (h.Now < until)
        {
            h.FireTimers(Step);
            if (pump.IsCompleted)
            {
                pump = h.StartPump();
            }
        }

        hung.Fail(Timeout());
        await h.FinishAsync(pump, "the sweep never finished after alpha's call timed out", suspend);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Step);

        var bravoFirst = h.HRclone.Calls.First(c => c.Operation == "unmount" && c.Target == "N:");
        Assert.Equal(arrival, bravoFirst.At);

        var escalations = h.EscalationTimes("bravo");
        Assert.Single(escalations);
        Assert.True(
            escalations[0] >= arrival.AddSeconds(Budget) && escalations[0] <= until + TimeSpan.FromSeconds(1),
            $"bravo's forced unmount came {(escalations[0] - arrival).TotalSeconds:0.##} s after arrival; expected after " +
            $"the budget and no later than alpha's call returning (30 s).");
    }
}
