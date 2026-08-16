using System.Net;
using System.Text;

namespace Bosun.Tests.Rclone.Fakes;

/// <summary>
/// Scriptable <see cref="HttpMessageHandler"/> backing the <see cref="HttpClient"/> injected into
/// <see cref="Bosun.Rclone.RcloneClient"/> under test -- no default-suite test performs real HTTP
/// (CLAUDE.md worktree-safety rules). Records every request's endpoint and raw body so tests can
/// assert on the ACTUAL JSON body <c>RcloneClient</c> sends, per the E3 brief's requirement to
/// assert on the body itself rather than a helper's return value.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _scriptedResponses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void EnqueueJson(HttpStatusCode statusCode, string json) => _scriptedResponses.Enqueue(new HttpResponseMessage(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new RecordedRequest(request.RequestUri!.AbsolutePath.TrimStart('/'), body));

        return _scriptedResponses.TryDequeue(out var next)
            ? next
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }
}

internal sealed record RecordedRequest(string Endpoint, string Body);
