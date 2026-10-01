namespace Bosun.Rclone;

/// <summary>
/// Thrown by <see cref="RcloneClient"/> when a single rc call did not complete within its
/// per-endpoint timeout (bs-x57). Deliberately an <see cref="RcloneRcException"/> and NOT an
/// <see cref="OperationCanceledException"/>: <see cref="System.Net.Http.HttpClient"/> reports its
/// own timeout as a <see cref="TaskCanceledException"/>, which IS an
/// <see cref="OperationCanceledException"/>, and every caller that filters
/// <c>when (ex is not OperationCanceledException)</c> to mean "shutdown, let it propagate" then
/// misreads a timeout as shutdown. In production (2026-09-28) that let one hung rc call over a dead
/// SSH channel escape <c>MountSupervisor</c>'s single channel-consumer loop and silently stop all
/// mount supervision. A typed timeout means a caller can never confuse "this call took too long"
/// with "the app is shutting down".
/// </summary>
public sealed class RcloneRcTimeoutException : RcloneRcException
{
    /// <summary>How long the call was allowed to run before it was abandoned.</summary>
    public TimeSpan Timeout { get; }

    public RcloneRcTimeoutException(string endpoint, TimeSpan timeout, Exception? innerException = null)
        : base(
            endpoint,
            httpStatusCode: null,
            $"rc call to '{endpoint}' timed out after {timeout.TotalSeconds:0.#}s",
            innerException)
    {
        Timeout = timeout;
    }
}
