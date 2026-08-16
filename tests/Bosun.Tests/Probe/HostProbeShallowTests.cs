using System.Net.Sockets;
using Bosun.Probe;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Probe.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Probe;

/// <summary>
/// Covers bs-pk4: <see cref="HostProbe.ProbeShallowAsync"/> distinguishes success, timeout,
/// connection refused, DNS failure, and cancellation (the brief: "why a probe failed has to
/// survive into the log"). No real socket anywhere here (CLAUDE.md worktree safety) — the
/// transport is <see cref="FakeTcpProbeTransport"/>, and the timeout path is driven through
/// <see cref="HostProbe"/>'s real timeout wiring via <see cref="FakeTimeProvider"/>, never a real
/// wall-clock wait.
/// </summary>
public sealed class HostProbeShallowTests
{
    [Fact]
    public async Task A_successful_connect_reports_Success()
    {
        var transport = new FakeTcpProbeTransport();
        transport.SucceedsImmediately();
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), new FakeTimeProvider());

        var result = await probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(ShallowProbeOutcome.Success, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Connection_refused_is_reported_distinctly_from_other_failures()
    {
        var transport = new FakeTcpProbeTransport();
        transport.ThrowsImmediately(new SocketException((int)SocketError.ConnectionRefused));
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), new FakeTimeProvider());

        var result = await probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(ShallowProbeOutcome.ConnectionRefused, result.Outcome);
        Assert.NotNull(result.Detail);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    [InlineData(SocketError.TryAgain)]
    public async Task Dns_resolution_failures_are_reported_as_DnsFailure(SocketError error)
    {
        var transport = new FakeTcpProbeTransport();
        transport.ThrowsImmediately(new SocketException((int)error));
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), new FakeTimeProvider());

        var result = await probe.ProbeShallowAsync("no-such-host.invalid", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(ShallowProbeOutcome.DnsFailure, result.Outcome);
    }

    [Fact]
    public async Task An_unrecognised_socket_error_is_reported_as_OtherFailure_not_silently_mislabelled()
    {
        var transport = new FakeTcpProbeTransport();
        transport.ThrowsImmediately(new SocketException((int)SocketError.NetworkUnreachable));
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), new FakeTimeProvider());

        var result = await probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(ShallowProbeOutcome.OtherFailure, result.Outcome);
    }

    [Fact]
    public async Task A_connect_that_never_completes_times_out_via_the_injected_TimeProvider_with_no_real_wait()
    {
        var transport = new FakeTcpProbeTransport();
        transport.Hangs();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), time);

        var probeTask = probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await probeTask;

        Assert.Equal(ShallowProbeOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task Advancing_time_short_of_the_timeout_does_not_time_out()
    {
        var transport = new FakeTcpProbeTransport();
        transport.Hangs();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), time);

        var probeTask = probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.False(probeTask.IsCompleted);
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_Cancelled_not_Timeout()
    {
        var transport = new FakeTcpProbeTransport();
        transport.Hangs();
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var probeTask = probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();
        var result = await probeTask;

        Assert.Equal(ShallowProbeOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Elapsed_reflects_simulated_time_not_real_wall_clock()
    {
        var transport = new FakeTcpProbeTransport();
        transport.Hangs();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(transport, new FakeRemoteRootLister(), time);

        var probeTask = probe.ProbeShallowAsync("example-nas.internal", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await probeTask;

        Assert.Equal(TimeSpan.FromSeconds(5), result.Elapsed);
    }

    private static HostProbe CreateProbe(FakeTcpProbeTransport transport, FakeRemoteRootLister lister, TimeProvider time) =>
        new(transport, lister, time, NullLogger<HostProbe>.Instance);
}
