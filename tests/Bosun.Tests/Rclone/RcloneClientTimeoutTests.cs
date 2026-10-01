using Bosun.Rclone;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;

namespace Bosun.Tests.Rclone;

/// <summary>
/// bs-x57: every <see cref="RcloneClient"/> call has an explicit timeout, driven by an injected
/// <see cref="TimeProvider"/> (no real waiting), and a timeout surfaces as the typed
/// <see cref="RcloneRcTimeoutException"/> -- never as a bare <see cref="TaskCanceledException"/>,
/// which callers filtering on <see cref="OperationCanceledException"/> misread as shutdown.
/// </summary>
public sealed class RcloneClientTimeoutTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Endpoints =>
        new() { "core/version", "config/get", "config/create", "mount/mount", "mount/unmount", "mount/listmounts", "operations/list" };

    private static TimeSpan ExpectedTimeout(string endpoint) => endpoint switch
    {
        "core/version" => RcloneClient.VersionTimeout,
        "config/get" or "config/create" => RcloneClient.ConfigTimeout,
        "mount/listmounts" => RcloneClient.ListMountsTimeout,
        "mount/unmount" => RcloneClient.UnmountTimeout,
        "mount/mount" => RcloneClient.MountTimeout,
        "operations/list" => RcloneClient.ListTimeout,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
    };

    private static Task Call(RcloneClient client, string endpoint, CancellationToken ct) => endpoint switch
    {
        "core/version" => client.GetVersionAsync(ct),
        "config/get" => client.GetConfigAsync("bosun-example-nas", ct),
        "config/create" => client.CreateConfigAsync("bosun-example-nas", "sftp", new Dictionary<string, string>(), ct),
        "mount/mount" => client.MountAsync(new RcloneMountRequest { Fs = "bosun-example-nas:/srv", MountPoint = "Q:" }, ct),
        "mount/unmount" => client.UnmountAsync("Q:", ct),
        "mount/listmounts" => client.ListMountsAsync(ct),
        "operations/list" => client.ListAsync("bosun-example-nas:", "", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
    };

    private static (RcloneClient Client, HangingHttpMessageHandler Handler, FakeTimeProvider Time) CreateHangingClient(
        Exception? handlerThrows = null)
    {
        var handler = new HangingHttpMessageHandler(handlerThrows);
        var time = new FakeTimeProvider();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5572/") };
        return (new RcloneClient(http, new RcloneRcCredential("bosun-test-user", "bosun-test-pass"), time), handler, time);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_call_that_never_completes_times_out_at_its_endpoints_timeout_with_a_typed_exception(string endpoint)
    {
        var (client, handler, time) = CreateHangingClient();
        var timeout = ExpectedTimeout(endpoint);

        var call = Call(client, endpoint, CancellationToken.None);

        Assert.Equal([endpoint], handler.Endpoints);
        Assert.False(call.IsCompleted);

        time.Advance(timeout - TimeSpan.FromMilliseconds(1));
        Assert.False(call.IsCompleted); // not a moment early

        time.Advance(TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<RcloneRcTimeoutException>(() => call.WaitAsync(Guard));

        Assert.Equal(endpoint, ex.Endpoint);
        Assert.Equal(timeout, ex.Timeout);
        Assert.Null(ex.HttpStatusCode);
        Assert.IsAssignableFrom<RcloneRcException>(ex);
        Assert.IsNotAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task The_caller_cancelling_its_own_token_is_still_a_cancellation_not_a_timeout()
    {
        var (client, _, time) = CreateHangingClient();
        using var cts = new CancellationTokenSource();

        var call = client.ListMountsAsync(cts.Token);
        cts.Cancel();

        // ThrowsAny<OperationCanceledException>: RcloneRcTimeoutException is not one, so this
        // passing proves the caller's cancellation was not converted into a timeout.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Guard));

        // And a timer that would have fired later does nothing to an already-finished call.
        time.Advance(RcloneClient.ListMountsTimeout);
    }

    [Fact]
    public async Task A_cancellation_raised_by_HttpClient_itself_is_also_reported_as_a_timeout()
    {
        // HttpClient.Timeout (if anyone configures one shorter than ours) surfaces as a
        // TaskCanceledException while the caller's token is untouched. It must not leak as a bare
        // OperationCanceledException either.
        var (client, _, _) = CreateHangingClient(handlerThrows: new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<RcloneRcTimeoutException>(() => client.GetVersionAsync(CancellationToken.None).WaitAsync(Guard));

        Assert.Equal("core/version", ex.Endpoint);
    }

    [Fact]
    public async Task GetConfigAsync_propagates_a_timeout_instead_of_reporting_the_remote_as_absent()
    {
        // An rc-level failure ("remote does not exist") returns null by design. A TIMEOUT says
        // nothing about whether the remote exists; returning null would make the provisioner
        // overwrite a good remote.
        var (client, _, time) = CreateHangingClient();

        var call = client.GetConfigAsync("bosun-example-nas", CancellationToken.None);
        time.Advance(RcloneClient.ConfigTimeout);

        await Assert.ThrowsAsync<RcloneRcTimeoutException>(() => call.WaitAsync(Guard));
    }

    [Fact]
    public async Task A_completed_call_is_not_affected_by_its_timer_firing_afterwards()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, """{"version":"v1.75.0"}""");
        var time = new FakeTimeProvider();
        var client = new RcloneClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5572/") },
            new RcloneRcCredential("bosun-test-user", "bosun-test-pass"),
            time);

        var version = await client.GetVersionAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("v1.75.0", version.Version);
    }

    [Fact]
    public void Timeouts_are_ordered_by_what_each_call_actually_does()
    {
        // Pins the reasoning in RcloneClient's comments: the in-memory calls are short, unmount is
        // longer than listmounts (dead-SSH-channel stall), mount is the slowest, and the
        // operations/list backstop sits above the default 5 s deep-probe timeout so it can never
        // pre-empt HostProbe's own bound.
        Assert.True(RcloneClient.VersionTimeout < RcloneClient.ListMountsTimeout);
        Assert.True(RcloneClient.ListMountsTimeout < RcloneClient.UnmountTimeout);
        Assert.True(RcloneClient.UnmountTimeout < RcloneClient.MountTimeout);
        Assert.True(RcloneClient.ListTimeout > TimeSpan.FromSeconds(Bosun.Configuration.GlobalConfig.DefaultProbeTimeoutSeconds));

        // All well under HttpClient's 100 s default, which the old code relied on.
        Assert.True(RcloneClient.MountTimeout < TimeSpan.FromSeconds(100));
    }
}
