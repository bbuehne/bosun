using System.Reflection;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// White-box tests on <c>MountSupervisor</c>'s transition table and its enforcement mechanism
/// (<c>SetState</c>) -- the "structurally hard to violate, not merely absent" requirement for
/// Invariant I1 ("Mounting is reachable only from Ready"). These are deliberately reflection-based:
/// the point is to prove the GUARD RAIL itself is real and would catch a bug, not just to observe
/// that current code paths happen to behave -- see <c>MountingLifecycleTests</c> for the
/// black-box/behavioural proof of the same invariant.
/// </summary>
public sealed class TransitionTableStructureTests
{
    [Fact]
    public void Mounting_is_reachable_only_from_Ready_in_the_transition_table()
    {
        var field = typeof(MountSupervisor).GetField("AllowedTransitions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var table = (IReadOnlyDictionary<MountState, MountState[]>)field!.GetValue(null)!;

        foreach (var (from, allowed) in table)
        {
            if (from == MountState.Ready)
            {
                Assert.Contains(MountState.Mounting, allowed);
            }
            else
            {
                Assert.DoesNotContain(MountState.Mounting, allowed);
            }
        }
    }

    [Theory]
    [InlineData(MountState.Disabled)]
    [InlineData(MountState.Unreachable)]
    [InlineData(MountState.Mounted)]
    [InlineData(MountState.Draining)]
    public async Task SetState_throws_when_asked_to_reach_Mounting_from_anything_other_than_Ready(MountState illegalFrom)
    {
        var config = HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("h"));
        var harness = new SupervisorHarness(config);
        await harness.StartAsync(); // populates the private host dictionary SetState needs to see

        var host = GetHostRuntime(harness.Supervisor, "h");
        SetPrivateState(host, illegalFrom);

        var setState = typeof(MountSupervisor).GetMethod("SetState", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(setState);

        var thrown = Assert.Throws<TargetInvocationException>(() =>
            setState!.Invoke(harness.Supervisor, [host, MountState.Mounting, "illegal-test-transition"]));

        var inner = Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Contains($"{illegalFrom} -> {MountState.Mounting}", inner.Message);
    }

    [Fact]
    public async Task SetState_permits_Ready_to_Mounting()
    {
        var config = HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("h"));
        var harness = new SupervisorHarness(config);
        await harness.StartAsync();

        var host = GetHostRuntime(harness.Supervisor, "h");
        SetPrivateState(host, MountState.Ready);

        var setState = typeof(MountSupervisor).GetMethod("SetState", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Must not throw.
        setState.Invoke(harness.Supervisor, [host, MountState.Mounting, "legal-test-transition"]);

        var stateField = host.GetType().GetField("State")!;
        Assert.Equal(MountState.Mounting, (MountState)stateField.GetValue(host)!);
    }

    // -- reflection helpers --------------------------------------------------------------------

    private static object GetHostRuntime(MountSupervisor supervisor, string hostKey)
    {
        var hostsField = typeof(MountSupervisor).GetField("hosts", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(hostsField);

        var dictionary = (System.Collections.IDictionary)hostsField!.GetValue(supervisor)!;
        var host = dictionary[hostKey];
        Assert.NotNull(host);
        return host!;
    }

    private static void SetPrivateState(object hostRuntime, MountState state)
    {
        var stateField = hostRuntime.GetType().GetField("State");
        Assert.NotNull(stateField);
        stateField!.SetValue(hostRuntime, state);
    }
}
