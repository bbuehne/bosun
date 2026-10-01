namespace Bosun.Tests.Rclone.Fakes;

/// <summary>
/// <see cref="HttpMessageHandler"/> that never produces a response on its own -- a stand-in for an
/// <c>rclone rcd</c> that has accepted the request but is blocked behind a dead SSH channel. It
/// honours cancellation exactly like a real handler (throws <see cref="TaskCanceledException"/>
/// when the request token fires), which is what makes the timeout path real rather than faked.
/// No real network, no real clock.
/// </summary>
internal sealed class HangingHttpMessageHandler : HttpMessageHandler
{
    private readonly Exception? _throwsInstead;

    public HangingHttpMessageHandler(Exception? throwsInstead = null) => _throwsInstead = throwsInstead;

    public List<string> Endpoints { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Endpoints.Add(request.RequestUri!.AbsolutePath.TrimStart('/'));

        if (_throwsInstead is not null)
        {
            throw _throwsInstead;
        }

        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable: Task.Delay(Infinite) only ends by cancellation");
    }
}
