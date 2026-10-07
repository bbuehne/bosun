using System.Net.Http;

namespace Bosun.Rclone;

/// <summary>
/// Classifies a failed rc call for logging (bs-qcs). A <em>kind</em> is the stable part of a
/// failure -- what repeats when the same fault recurs -- so the log can say it once and not once
/// per tick. An <em>expected</em> failure is one the log line already explains in words (HTTP 401,
/// connection refused, a timeout); its stack trace adds nothing, and the traces were most of a
/// 200 MB day.
/// </summary>
public static class RcFaultKinds
{
    public static string Kind(Exception failure) => failure switch
    {
        RcloneRcTimeoutException => "rc timeout",
        RcloneRcException { HttpStatusCode: { } code } => $"HTTP {code}",
        RcloneRcException => "unusable rc response",
        HttpRequestException => "connection refused or reset",
        _ => failure.GetType().Name,
    };

    /// <summary>False for an exception type nobody planned for, whose stack trace is worth keeping.</summary>
    public static bool IsExpected(Exception failure) => failure is RcloneRcException or HttpRequestException;

    /// <summary>The exception to attach to a log entry: none for an expected failure.</summary>
    public static Exception? ForLog(Exception failure) => IsExpected(failure) ? null : failure;
}
