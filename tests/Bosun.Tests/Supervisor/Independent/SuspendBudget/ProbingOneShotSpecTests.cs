using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendBudget;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 10 (bs-but), written from the spec: <c>Probing</c> is one-shot. A
/// host enters it only from <c>Disabled</c>, and leaves it only for <c>Ready</c> or
/// <c>Unreachable</c>. Recurring probes -- the idle probe on <c>Ready</c>/<c>Unreachable</c> (backoff
/// ladder, re-probe on resume or network change, "retry now") and the shallow and deep probes on
/// <c>Mounted</c> -- run in place and log no transition. Only a probe whose result changes the state
/// logs one.
/// </summary>
/// <remarks>
/// The transition log is the primary triage artefact (docs/OPERATIONS.md). If every recurring probe
/// logged <c>X -&gt; Probing -&gt; X</c>, the log would fill with one pair per probe per host and the
/// transitions that matter would be buried. Every test here asserts on
/// <see cref="MountSupervisor.GetTransitionHistory"/> and guards against passing vacuously by also
/// asserting the probes really ran.
/// </remarks>
public sealed class ProbingOneShotSpecTests
{
    /// <summary>
    /// <b>Protects:</b> the structural half of rule 10 over a long, mixed scenario: every
    /// <c>-&gt; Probing</c> comes from <c>Disabled</c>, every <c>Probing -&gt;</c> goes to
    /// <c>Ready</c> or <c>Unreachable</c>, and a host enters <c>Probing</c> no more often than it has
    /// been <c>Disabled</c>.
    /// <b>Catches:</b> any one path (resume, network change, retry now, the backoff ladder, the parked
    /// host's idle probe, the mounted cadence) that still routes a recurring probe through
    /// <c>Probing</c>.
    /// </summary>
    [Fact]
    public async Task Probing_is_entered_only_from_Disabled_and_left_only_for_Ready_or_Unreachable()
    {
        var h = new SuspendBudgetHarness(HostFixtures.Build(
            HostFixtures.Global(suspendUnmountTimeoutSeconds: 3),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:"),
            HostFixtures.Persistent("flaky", probeIntervalSeconds: 30, drive: "F:"),
            HostFixtures.OnDemand("archive", probeIntervalSeconds: 30, drive: "Q:")));
        var flaky = SuspendBudgetHarness.HostnameOf("flaky");
        h.Probe.SetShallow(flaky, ShallowProbeOutcome.ConnectionRefused);

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync("flaky"));
        h.Probe.SetShallow(flaky, ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));
        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("prod"));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync("prod"));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        var history = h.Supervisor.GetTransitionHistory();
        var log = string.Join(Environment.NewLine, history.Select(t => $"{t.HostKey}: {t.From} -> {t.To} ({t.Trigger})"));

        Assert.All(history.Where(t => t.To == MountState.Probing), t =>
            Assert.True(t.From == MountState.Disabled, $"{t.HostKey} entered Probing from {t.From}.{Environment.NewLine}{log}"));
        Assert.All(history.Where(t => t.From == MountState.Probing), t =>
            Assert.True(
                t.To is MountState.Ready or MountState.Unreachable,
                $"{t.HostKey} left Probing for {t.To}.{Environment.NewLine}{log}"));

        foreach (var host in new[] { "prod", "flaky", "archive" })
        {
            var entries = h.Transitions(host);
            var intoProbing = entries.Count(t => t.To == MountState.Probing);
            var intoDisabled = entries.Count(t => t.To == MountState.Disabled);

            // +1 for the startup enable, whose starting Disabled is bootstrap state, not a transition.
            Assert.True(intoProbing <= intoDisabled + 1, $"{host}: {intoProbing} entries into Probing vs {intoDisabled} into Disabled.{Environment.NewLine}{log}");
        }

        // Not vacuous: the scenario did run many probes.
        Assert.True(h.Probe.TotalShallowCalls > 50, $"only {h.Probe.TotalShallowCalls} shallow probes ran");
    }

    /// <summary>
    /// <b>Protects:</b> rule 10, the backoff ladder's idle probe on an <c>Unreachable</c> host runs in
    /// place.
    /// <b>Catches:</b> the pre-bs-but shape, <c>Unreachable -&gt; Probing -&gt; Unreachable</c> per
    /// rung, which logs two transitions per failed probe for as long as the host is down.
    /// </summary>
    [Fact]
    public async Task The_backoff_ladder_on_an_unreachable_host_logs_no_transitions()
    {
        var h = await UnreachablePersistentAsync();
        var transitions = h.Transitions("prod").Count;
        var probes = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod"));

        await h.AdvanceAsync(TimeSpan.FromMinutes(30));

        Assert.True(h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod")) >= probes + 5, "the ladder should have probed several times");
        Assert.Equal(transitions, h.Transitions("prod").Count);
    }

    /// <summary>
    /// <b>Protects:</b> rule 10, the shallow and deep probes on a <c>Mounted</c> host run in place.
    /// <b>Catches:</b> a mounted cadence that logs <c>Mounted -&gt; Probing -&gt; Mounted</c> (or any
    /// other pair) per tick -- for the life of every mount.
    /// </summary>
    [Fact]
    public async Task Recurring_shallow_and_deep_probes_on_a_mounted_host_log_no_transitions()
    {
        var h = new SuspendBudgetHarness(HostFixtures.Build(
            HostFixtures.Global(), HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        var transitions = h.Transitions("prod").Count;
        var shallow = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod"));
        var deep = h.Probe.DeepCount("prod");

        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.True(h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod")) >= shallow + 60, "shallow cadence should have run");
        Assert.True(h.Probe.DeepCount("prod") >= deep + 10, "deep cadence should have run");
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(transitions, h.Transitions("prod").Count);
    }

    /// <summary>
    /// <b>Protects:</b> rule 10 for the idle probe on a <c>Ready</c> host, using rule 9's parked
    /// persistent host (which "keeps probing").
    /// <b>Catches:</b> <c>Ready -&gt; Probing -&gt; Ready</c> per idle probe.
    /// </summary>
    [Fact]
    public async Task The_idle_probe_on_a_parked_Ready_host_logs_no_transitions()
    {
        var h = new SuspendBudgetHarness(HostFixtures.Build(
            HostFixtures.Global(), HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("prod"));
        Assert.Equal(MountState.Ready, h.State("prod"));
        var transitions = h.Transitions("prod").Count;
        var probes = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod"));

        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.True(h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod")) > probes, "a parked host keeps probing (rule 9)");
        Assert.Equal(transitions, h.Transitions("prod").Count);
    }

    /// <summary>
    /// <b>Protects:</b> "Only a probe whose result changes the state produces a transition --
    /// <c>Unreachable -&gt; Ready</c>".
    /// <b>Catches:</b> recovery logged as <c>Unreachable -&gt; Probing -&gt; Ready</c>, or a recovery
    /// that is not logged at all.
    /// </summary>
    [Fact]
    public async Task Recovery_from_Unreachable_is_logged_as_a_direct_Unreachable_to_Ready_transition()
    {
        var h = await UnreachablePersistentAsync();
        var before = h.Transitions("prod").Count;

        h.Probe.SetShallow(SuspendBudgetHarness.HostnameOf("prod"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        var after = h.Transitions("prod").Skip(before).ToList();
        Assert.NotEmpty(after);
        Assert.Equal((MountState.Unreachable, MountState.Ready), (after[0].From, after[0].To));
        Assert.DoesNotContain(after, t => t.To == MountState.Probing || t.From == MountState.Probing);
        Assert.Equal(MountState.Mounted, h.State("prod"));
    }

    /// <summary>
    /// <b>Protects:</b> rule 10, "an immediate re-probe on ... network change" runs in place.
    /// <b>Catches:</b> a network change that routes every idle host through <c>Probing</c>. On a
    /// flapping link that is two log lines per host per network event.
    /// </summary>
    [Fact]
    public async Task A_network_change_re_probes_an_unreachable_host_in_place()
    {
        var h = await UnreachablePersistentAsync();
        var transitions = h.Transitions("prod").Count;
        var probes = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod"));

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());

        Assert.Equal(probes + 1, h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod")));
        Assert.Equal(transitions, h.Transitions("prod").Count);
    }

    /// <summary>
    /// <b>Protects:</b> rule 10, "retry now" runs in place.
    /// <b>Catches:</b> the user's "retry now" click logging <c>Unreachable -&gt; Probing -&gt;
    /// Unreachable</c> when the host is still down.
    /// </summary>
    [Fact]
    public async Task Retry_now_on_a_still_unreachable_host_probes_in_place()
    {
        var h = await UnreachablePersistentAsync();
        var transitions = h.Transitions("prod").Count;
        var probes = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod"));

        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync("prod"));

        Assert.Equal(probes + 1, h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod")));
        Assert.Equal(transitions, h.Transitions("prod").Count);
    }

    /// <summary>
    /// <b>Protects:</b> rule 5 / rule 10, resume re-probes an <c>Unreachable</c> host in place.
    /// <b>Catches:</b> resume treating every enabled host alike and sending the unreachable one
    /// through <c>Probing</c>.
    /// </summary>
    [Fact]
    public async Task Resume_re_probes_an_unreachable_host_in_place()
    {
        var h = await UnreachablePersistentAsync();
        await h.SuspendAsync();
        var transitions = h.Transitions("prod").Count;
        var probes = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod"));

        await h.ResumeAsync();

        Assert.Equal(probes + 1, h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("prod")));
        Assert.Equal(transitions, h.Transitions("prod").Count);
    }

    /// <summary>
    /// <b>Protects:</b> rule 10's positive case: "on resume for a host the suspend left
    /// <c>Disabled</c>" the initial check goes through <c>Probing</c>.
    /// <b>Catches:</b> an over-correction that removes <c>Probing</c> everywhere, so a suspended
    /// host goes <c>Disabled -&gt; Ready</c> or <c>Disabled -&gt; Mounting</c> with no logged probe.
    /// </summary>
    [Fact]
    public async Task Resume_sends_a_host_the_suspend_left_Disabled_through_Probing_once()
    {
        var h = new SuspendBudgetHarness(HostFixtures.Build(
            HostFixtures.Global(), HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await h.StartAsync();
        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State("prod"));
        var before = h.Transitions("prod").Count;

        await h.ResumeAsync();

        var after = h.Transitions("prod").Skip(before).Select(t => (t.From, t.To)).ToList();
        Assert.Equal(
            [
                (MountState.Disabled, MountState.Probing),
                (MountState.Probing, MountState.Ready),
                (MountState.Ready, MountState.Mounting),
                (MountState.Mounting, MountState.Mounted),
            ],
            after);
    }

    private static async Task<SuspendBudgetHarness> UnreachablePersistentAsync()
    {
        var h = new SuspendBudgetHarness(HostFixtures.Build(
            HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        h.Probe.SetShallow(SuspendBudgetHarness.HostnameOf("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("prod"));
        return h;
    }
}
