#if DEBUG
using Bosun.Diagnostics.FaultInjection;
using Bosun.Rclone;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Diagnostics.FaultInjection;

/// <summary>
/// bs-uzk: the decorator behind <c>BOSUN_FAULT=rcd-unavailable-at-resume</c>. A fake rclone client,
/// a fake system-event source and a fake clock: nothing here reaches a real rcd, a drive letter or
/// the wall clock.
/// </summary>
public sealed class FaultInjectingRcloneClientTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(120);

    private readonly FakeRcloneClient _inner = new();
    private readonly FakeSystemEventSource _events = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

    private FaultInjectingRcloneClient Create() =>
        new(_inner, _events, _time, Duration, NullLogger.Instance);

    private static RcloneMountRequest MountRequest() =>
        new() { Fs = "bosun-example:/", MountPoint = "P:" };

    [Fact]
    public async Task BeforeAnyResume_EveryCallIsForwarded()
    {
        using var client = Create();

        await client.GetVersionAsync(CancellationToken.None);
        await client.MountAsync(MountRequest(), CancellationToken.None);

        Assert.Single(_inner.GetVersionCalls);
        Assert.Single(_inner.MountCalls);
    }

    [Fact]
    public async Task AfterAResume_CoreVersionFailsLikeRcdNotAnswering_AndIsNotForwarded()
    {
        using var client = Create();
        _events.RaiseResume();

        var ex = await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVersionAsync(CancellationToken.None));

        Assert.Equal("core/version", ex.Endpoint);
        Assert.Contains("BOSUN_FAULT=rcd-unavailable-at-resume", ex.Message);
        Assert.Empty(_inner.GetVersionCalls);
    }

    [Fact]
    public async Task AfterAResume_MountFails_AndRcdIsNeverAskedToMount()
    {
        using var client = Create();
        _events.RaiseResume();

        var ex = await Assert.ThrowsAsync<RcloneRcException>(() => client.MountAsync(MountRequest(), CancellationToken.None));

        Assert.Equal("mount/mount", ex.Endpoint);
        Assert.Empty(_inner.MountCalls);
    }

    [Fact]
    public async Task AfterAResume_UnmountListAndTheOtherCallsStillWork()
    {
        using var client = Create();
        _events.RaiseResume();

        await client.UnmountAsync("P:", CancellationToken.None);
        await client.ListMountsAsync(CancellationToken.None);
        await client.ListAsync("bosun-example:/", string.Empty, CancellationToken.None);
        await client.ListVfsAsync(CancellationToken.None);
        await client.GetConfigAsync("bosun-example", CancellationToken.None);

        Assert.Single(_inner.UnmountCalls);
        Assert.Equal(1, _inner.ListMountsCalls);
        Assert.Single(_inner.ListCalls);
        Assert.Single(_inner.GetConfigCalls);
    }

    [Fact]
    public async Task TheWindow_LastsExactlyTheConfiguredDuration()
    {
        using var client = Create();
        _events.RaiseResume();

        _time.Advance(Duration - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVersionAsync(CancellationToken.None));

        _time.Advance(TimeSpan.FromSeconds(1));
        var version = await client.GetVersionAsync(CancellationToken.None);

        Assert.NotNull(version);
        Assert.Single(_inner.GetVersionCalls);
    }

    [Fact]
    public async Task AfterTheWindow_MountIsForwardedAgain()
    {
        using var client = Create();
        _events.RaiseResume();
        _time.Advance(Duration);

        await client.MountAsync(MountRequest(), CancellationToken.None);

        Assert.Single(_inner.MountCalls);
    }

    [Fact]
    public async Task ASecondResume_RestartsTheWindow()
    {
        using var client = Create();
        _events.RaiseResume();
        _time.Advance(Duration);
        await client.GetVersionAsync(CancellationToken.None);

        _events.RaiseResume();

        await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVersionAsync(CancellationToken.None));
        _time.Advance(Duration);
        await client.GetVersionAsync(CancellationToken.None);
        Assert.Equal(2, _inner.GetVersionCalls.Count);
    }

    [Fact]
    public async Task OtherEvents_DoNotOpenTheWindow()
    {
        using var client = Create();

        _events.RaiseSuspend();
        _events.RaiseNetworkAddressChanged();
        _events.RaiseSessionLock();

        await client.GetVersionAsync(CancellationToken.None);
        Assert.Single(_inner.GetVersionCalls);
    }

    [Fact]
    public async Task AfterDispose_AResumeNoLongerOpensTheWindow()
    {
        var client = Create();
        client.Dispose();

        _events.RaiseResume();

        await client.GetVersionAsync(CancellationToken.None);
        Assert.Single(_inner.GetVersionCalls);
    }
}
#endif
