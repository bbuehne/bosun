namespace Bosun.Rclone;

/// <summary>
/// Thrown by <see cref="RcloneClient"/> when the rclone rc HTTP API itself reports a failure --
/// a non-2xx response, or a response body rclone's rc layer marks as an error. Deliberately
/// distinct from a bare <see cref="System.Net.Http.HttpRequestException"/> (which means the
/// HTTP call could not even be made -- rcd unreachable, connection refused, DNS failure): that
/// distinction matters to callers such as <see cref="RcloneRemoteProvisioner"/>, which treats
/// "the rc API answered and said no" very differently from "we could not talk to rcd at all".
/// </summary>
public sealed class RcloneRcException : Exception
{
    /// <summary>The rc endpoint path this call was made against, e.g. <c>config/get</c>.</summary>
    public string Endpoint { get; }

    /// <summary>The HTTP status code returned, when the failure came from an HTTP response.
    /// <see langword="null"/> when the failure was purely in the response body's shape (e.g. it
    /// was not valid JSON) despite a 2xx status.</summary>
    public int? HttpStatusCode { get; }

    public RcloneRcException(string endpoint, int? httpStatusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
        HttpStatusCode = httpStatusCode;
    }
}
