using System.Security.Cryptography;
using System.Text;

namespace Bosun.Rclone;

/// <summary>
/// Cryptographically random Basic-auth credential for the rclone rc HTTP API (bs-ard).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Verified against a real rclone v1.75.0 binary: every rc endpoint
/// except <c>core/version</c> returns HTTP 403 ("authentication must be set up on the rc server
/// ... or the --rc-no-auth flag must be in use") unless the rcd process is started with rc
/// credentials configured. The previous assumption -- that a loopback-only bind needed no auth
/// flag at all -- was wrong for this version; see the corrected remarks on
/// <see cref="Process.RcloneProcessService.BuildStartInfo"/>, which used to cite
/// https://rclone.org/commands/rclone_rcd/ for that claim.
/// </para>
/// <para>
/// <b>Why a random credential, not <c>--rc-no-auth</c>.</b> <c>--rc-no-auth</c> disables
/// authentication for the rc HTTP API entirely, for every local process that can reach the
/// loopback port -- including <c>mount/mount</c> and <c>mount/unmount</c>. A per-launch random
/// secret costs one <see cref="RandomNumberGenerator"/> call and removes that surface instead of
/// living with it.
/// </para>
/// <para>
/// <b>Lifetime: per Bosun-process-launch, not per <c>rclone rcd</c> restart.</b> One instance of
/// this type is created once, at DI wiring time (<see cref="Hosting.BosunHostFactory"/>), and
/// lives for the lifetime of the Bosun process (a singleton). <see cref="Process.RcloneProcessService"/>
/// passes the SAME instance's credentials as environment variables to EVERY <c>rclone rcd</c>
/// launch attempt it makes -- including restarts after an unexpected exit (see
/// <see cref="Process.RcloneProcessService"/>'s "restart on death" behaviour). This matters:
/// <see cref="RcloneClient"/> is also a singleton, constructed once, and configures its
/// <see cref="System.Net.Http.HttpClient"/>'s <c>Authorization</c> header exactly once at
/// construction. If the secret rotated on every restart, that header would go stale the moment
/// <c>rclone rcd</c> restarted, and every rc call would start failing with 401 until Bosun itself
/// were relaunched -- a subtle bug that would only surface after a crash, i.e. exactly when the
/// supervisor most needs the rc API to keep working. A single secret for the whole Bosun process
/// lifetime, reused across every child-process restart, avoids that class of bug entirely: the
/// two singletons are wired from the same <see cref="RcloneRcCredential"/> instance and never
/// need to resynchronise.
/// </para>
/// <para>
/// <b>Never logged, never persisted.</b> <see cref="Password"/> must never reach a log line, an
/// exception message, or a process command line -- a command line is readable by any process
/// running as the same user via <c>Win32_Process.CommandLine</c> (Bosun's own
/// <c>ISessionMonitor</c> uses exactly that API to enumerate <c>ssh.exe</c>, which is why this
/// credential is passed to the child via environment variables, never <c>--rc-user</c>/
/// <c>--rc-pass</c> on the command line -- see <see cref="Process.RcloneProcessService.BuildStartInfo"/>).
/// <see cref="ToString"/> is overridden to redact the secret so an accidental
/// <c>logger.LogInformation("{Credential}", credential)</c> cannot leak it either. The secret is
/// held only in memory for the process lifetime and is never written to disk.
/// </para>
/// </remarks>
public sealed class RcloneRcCredential
{
    /// <summary>Fixed, non-secret identifier -- only <see cref="Password"/> carries entropy.</summary>
    public string UserName { get; }

    public string Password { get; }

    public RcloneRcCredential(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        UserName = userName;
        Password = password;
    }

    /// <summary>
    /// Generates a new credential: fixed username, 32 bytes (256 bits) of
    /// <see cref="RandomNumberGenerator"/> output, base64-encoded as the password. Deliberately
    /// <see cref="RandomNumberGenerator"/>, never <see cref="Random"/> -- this secret must be
    /// cryptographically unguessable, not merely varied.
    /// </summary>
    public static RcloneRcCredential CreateRandom() =>
        new("bosun", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <summary>
    /// The value that goes after <c>Authorization: Basic </c> -- base64 of <c>user:pass</c>, per
    /// RFC 7617.
    /// </summary>
    public string ToBasicAuthHeaderValue() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}"));

    /// <summary>
    /// Deliberately redacted -- see the class remarks on never logging <see cref="Password"/>.
    /// Any accidental <c>{Credential}</c> in a log message template lands on this text, not the
    /// real secret.
    /// </summary>
    public override string ToString() => "RcloneRcCredential(redacted)";
}
