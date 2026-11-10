using System.Text.RegularExpressions;

namespace Bosun.Rclone;

/// <summary>
/// Why rclone refused to connect to a host because of SSH host-key verification (ADR-025, bs-k9x).
/// Each case needs a different action from the maintainer, and none of them is "the host is
/// unreachable" -- the host answered, and Bosun declined to trust it (Invariant I1).
/// </summary>
public enum HostKeyFailureKind
{
    /// <summary>The host's key is not in <c>known_hosts</c>. Fixed by connecting once through the
    /// host's Terminal profile (<c>ssh.exe</c> adds the key).</summary>
    UnknownKey,

    /// <summary><c>known_hosts</c> has a key for this host and the server presented a different one.
    /// Either the server's key really changed or something else is answering at its address.</summary>
    ChangedKey,

    /// <summary>The <c>known_hosts</c> file Bosun points rclone at does not exist, so no host can be
    /// verified.</summary>
    KnownHostsMissing,
}

/// <summary>
/// Recognises a host-key refusal in the text of a failed rc call and supplies the user-facing
/// sentence for it (ADR-025 Decisions 2 and 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the texts come from.</b> Read from source, not observed from a live rclone (bs-k9x):
/// </para>
/// <list type="bullet">
/// <item><c>knownhosts: key is unknown</c> and <c>knownhosts: key mismatch</c> are
/// <c>KeyError.Error()</c> in <c>golang.org/x/crypto/ssh/knownhosts/knownhosts.go</c> (empty
/// <c>Want</c> is unknown, non-empty is a mismatch). The callback's error is returned unchanged
/// from the SSH handshake, and <c>ssh.NewClientConn</c> (<c>ssh/client.go</c>) wraps it as
/// <c>ssh: handshake failed: %w</c>; rclone's sftp backend (<c>backend/sftp/sftp.go</c>,
/// <c>sftpConnection</c>) wraps that again as <c>couldn't connect SSH: %w</c>.</item>
/// <item>A missing file: rclone calls <c>knownhosts.New(env.ShellExpand(known_hosts_file))</c> when it
/// builds the SSH config and wraps a failure as <c>couldn't parse known_hosts_file: %w</c>;
/// <c>knownhosts.New</c> returns the <c>os.Open</c> error unchanged. On Windows that is
/// <c>open &lt;path&gt;: The system cannot find the file specified.</c> (or
/// <c>...find the path specified.</c> when <c>.ssh</c> itself is absent). The Windows wording is
/// Go's rendering of the Win32 error and is inferred, not read from a source line; the POSIX
/// <c>no such file or directory</c> is accepted too.</item>
/// <item>The rc error envelope that carries these (the text before them, for <c>operations/list</c>
/// and <c>mount/mount</c>) is not matched on at all, only the Go-library text above, so a change to
/// how rclone words its own prefix cannot break the match. What rclone puts in front of it was not
/// verified.</item>
/// </list>
/// <para>
/// <b>Why not a substring of "known".</b> "unknown", a host named <c>knownhosts.example.net</c> and a
/// path containing <c>known_hosts</c> all contain it. Each pattern is the whole Go-library phrase,
/// anchored to the text that precedes it (<c>handshake failed: </c>, <c>couldn't parse
/// known_hosts_file: open ...</c>).
/// </para>
/// <para>
/// Not classified here: <c>knownhosts: key is revoked</c> (a <c>@revoked</c> line; not one of the
/// three ADR-025 cases) and any other <c>couldn't parse known_hosts_file</c> failure (a file that
/// exists but cannot be read). Both fall through to the ordinary failure text, which carries rclone's
/// own words.
/// </para>
/// </remarks>
public static class HostKeyFailure
{
    // NonBacktracking: matched against text that comes from a remote server's error path, so
    // linear-time matching is worth the loss of lookarounds; the patterns use the word-boundary anchor, which it supports.
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex Unknown = new(
        @"ssh: handshake failed: knownhosts: key is unknown\b", Options);

    private static readonly Regex Changed = new(
        @"ssh: handshake failed: knownhosts: key mismatch\b", Options);

    private static readonly Regex Missing = new(
        @"couldn't parse known_hosts_file: open [^\r\n]*: (?:The system cannot find the (?:file|path) specified\.|no such file or directory)",
        Options);

    /// <summary>The path as shown to the user. Deliberately the <c>%USERPROFILE%</c> form, never the
    /// expanded one: the text lands in the window, the tray and the diagnostics bundle.</summary>
    public const string KnownHostsDisplayPath = @"%USERPROFILE%\.ssh\known_hosts";

    /// <summary>The host-key case <paramref name="failureText"/> describes, or <see langword="null"/>
    /// when it is not a host-key refusal (a refused connection, a timeout, an auth failure, anything
    /// else).</summary>
    public static HostKeyFailureKind? Classify(string? failureText)
    {
        if (string.IsNullOrEmpty(failureText))
        {
            return null;
        }

        if (Unknown.IsMatch(failureText))
        {
            return HostKeyFailureKind.UnknownKey;
        }

        if (Changed.IsMatch(failureText))
        {
            return HostKeyFailureKind.ChangedKey;
        }

        if (Missing.IsMatch(failureText))
        {
            return HostKeyFailureKind.KnownHostsMissing;
        }

        return null;
    }

    /// <summary>The sentence the window, tray and diagnostics bundle show for <paramref name="kind"/>
    /// (ADR-025 Decisions 2 and 3). Contains no key material, no profile path and no address.</summary>
    public static string Describe(HostKeyFailureKind kind) => kind switch
    {
        HostKeyFailureKind.UnknownKey =>
            "Host key not verified: connect once through this host's Terminal profile, then Bosun will mount it.",
        HostKeyFailureKind.ChangedKey =>
            "Host key does not match known_hosts. Check the server; if its key really changed, remove the old line from known_hosts.",
        HostKeyFailureKind.KnownHostsMissing =>
            $"No known_hosts file at {KnownHostsDisplayPath}, so no host can be verified.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown host-key failure kind."),
    };
}
