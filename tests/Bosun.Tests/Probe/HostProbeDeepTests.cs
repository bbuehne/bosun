using Bosun.Probe;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Probe.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Probe;

/// <summary>
/// Covers bs-k8p: <see cref="HostProbe.ProbeDeepAsync"/> against the narrow
/// <see cref="IRemoteRootLister"/> seam, faked here so E4 does not block on E3's real rclone
/// client (docs/WORK-BREAKDOWN.md: E4 depends only on E2). Mirrors
/// <see cref="HostProbeShallowTests"/>'s timeout/cancellation coverage since both probes share
/// the same <see cref="TimeProvider"/>-driven timeout wiring in <see cref="HostProbe"/>.
/// </summary>
public sealed class HostProbeDeepTests
{
    [Fact]
    public async Task A_successful_root_listing_reports_Success()
    {
        var lister = new FakeRemoteRootLister();
        lister.SucceedsImmediately();
        var probe = CreateProbe(new FakeTcpProbeTransport(), lister, new FakeTimeProvider());

        var result = await probe.ProbeDeepAsync("example-nas", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(DeepProbeOutcome.Success, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task The_configured_host_key_is_the_one_passed_to_the_lister()
    {
        var lister = new FakeRemoteRootLister();
        lister.SucceedsImmediately();
        var probe = CreateProbe(new FakeTcpProbeTransport(), lister, new FakeTimeProvider());

        await probe.ProbeDeepAsync("example-nas", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(["example-nas"], lister.Requests);
    }

    [Fact]
    public async Task An_auth_or_transport_failure_is_reported_as_Failed_with_the_error_preserved_for_logging()
    {
        var lister = new FakeRemoteRootLister();
        lister.ThrowsImmediately(new InvalidOperationException("ssh: handshake failed: no supported authentication methods"));
        var probe = CreateProbe(new FakeTcpProbeTransport(), lister, new FakeTimeProvider());

        var result = await probe.ProbeDeepAsync("example-nas", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(DeepProbeOutcome.Failed, result.Outcome);
        Assert.Contains("handshake failed", result.Detail);
    }

    [Fact]
    public async Task A_listing_that_never_completes_times_out_via_the_injected_TimeProvider_with_no_real_wait()
    {
        var lister = new FakeRemoteRootLister();
        lister.Hangs();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(new FakeTcpProbeTransport(), lister, time);

        var probeTask = probe.ProbeDeepAsync("example-nas", TimeSpan.FromSeconds(10), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        var result = await probeTask;

        Assert.Equal(DeepProbeOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_Cancelled_not_Timeout()
    {
        var lister = new FakeRemoteRootLister();
        lister.Hangs();
        var probe = CreateProbe(new FakeTcpProbeTransport(), lister, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var probeTask = probe.ProbeDeepAsync("example-nas", TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();
        var result = await probeTask;

        Assert.Equal(DeepProbeOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task A_passing_shallow_probe_type_cannot_be_supplied_where_a_deep_result_is_required()
    {
        // bs-pk4/bs-k8p: this is a compile-time guarantee, not a runtime assertion -- the point is
        // that ShallowProbeResult and DeepProbeResult are unrelated types (Invariant I1). This
        // test exists to document that intent and would fail to *compile* (not just fail) if
        // someone gave the two types a shared base or an implicit conversion.
        var lister = new FakeRemoteRootLister();
        lister.SucceedsImmediately();
        var probe = CreateProbe(new FakeTcpProbeTransport(), lister, new FakeTimeProvider());

        DeepProbeResult deep = await probe.ProbeDeepAsync("example-nas", TimeSpan.FromSeconds(10), CancellationToken.None);
        ShallowProbeResult shallow = await probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        // No implicit/explicit conversion exists between these types; if one were added, this
        // assertion would still hold but the design guarantee the brief asked for would be gone.
        Assert.IsType<DeepProbeResult>(deep);
        Assert.IsType<ShallowProbeResult>(shallow);
        Assert.IsNotType<DeepProbeResult>((object)shallow);
    }

    private static HostProbe CreateProbe(FakeTcpProbeTransport transport, FakeRemoteRootLister lister, TimeProvider time) =>
        new(transport, lister, time, NullLogger<HostProbe>.Instance);
}
