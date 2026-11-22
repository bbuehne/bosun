using System.Text.RegularExpressions;
using Bosun.Rclone;

namespace Bosun.Tests.Rclone;

/// <summary>
/// ADR-025 (bs-k9x): recognising rclone's host-key refusals in the text of a failed rc call, and the
/// sentences shown for them. The raw texts below are the Go-library phrases (x/crypto
/// <c>knownhosts.KeyError</c>, <c>ssh.NewClientConn</c>'s "handshake failed" wrapper, rclone's
/// "couldn't parse known_hosts_file" wrapper) read from their source; the words around them are
/// representative envelopes, because the classifier deliberately does not depend on those.
/// </summary>
public sealed class HostKeyFailureTests
{
    private const string UnknownCore = "couldn't connect SSH: ssh: handshake failed: knownhosts: key is unknown";
    private const string ChangedCore = "couldn't connect SSH: ssh: handshake failed: knownhosts: key mismatch";
    private const string MissingCore =
        @"couldn't parse known_hosts_file: open C:\Users\someone\.ssh\known_hosts: The system cannot find the file specified.";

    /// <summary>Envelopes the failure can arrive in: the bare Go text, the rc error shape
    /// <c>RcloneClient</c> builds for an HTTP error, and what the deep probe and mount path add.</summary>
    public static TheoryData<string> Envelopes() => new()
    {
        "{0}",
        "rc call to 'operations/list' failed: failed to create file system for \"bosun-prod:\": {0}",
        "rc call to 'mount/mount' failed: mount FAILED: failed to create file system for \"bosun-prod:/srv\": {0}",
        "deep probe failed entering Mounting: rc call to 'operations/list' failed: {0}",
    };

    [Theory]
    [MemberData(nameof(Envelopes))]
    public void An_unknown_key_is_recognised_inside_every_envelope(string envelope) =>
        Assert.Equal(HostKeyFailureKind.UnknownKey, HostKeyFailure.Classify(string.Format(envelope, UnknownCore)));

    [Theory]
    [MemberData(nameof(Envelopes))]
    public void A_changed_key_is_recognised_inside_every_envelope(string envelope) =>
        Assert.Equal(HostKeyFailureKind.ChangedKey, HostKeyFailure.Classify(string.Format(envelope, ChangedCore)));

    [Theory]
    [MemberData(nameof(Envelopes))]
    public void A_missing_known_hosts_file_is_recognised_inside_every_envelope(string envelope) =>
        Assert.Equal(HostKeyFailureKind.KnownHostsMissing, HostKeyFailure.Classify(string.Format(envelope, MissingCore)));

    [Theory]
    [InlineData("The system cannot find the path specified.")]
    [InlineData("no such file or directory")]
    public void A_missing_file_is_recognised_under_the_other_wordings_Go_produces(string osText) =>
        Assert.Equal(
            HostKeyFailureKind.KnownHostsMissing,
            HostKeyFailure.Classify($@"couldn't parse known_hosts_file: open C:\Users\someone\.ssh\known_hosts: {osText}"));

    [Theory]
    [InlineData("dial tcp 192.0.2.10:22: connectex: No connection could be made because the target machine actively refused it.")]
    [InlineData("couldn't connect SSH: dial tcp 192.0.2.10:22: i/o timeout")]
    [InlineData("couldn't connect SSH: ssh: handshake failed: ssh: unable to authenticate, attempted methods [none publickey], no supported methods remain")]
    [InlineData("couldn't connect SSH: ssh: handshake failed: EOF")]
    [InlineData("failed to parse private key file: ssh: no key found")]
    [InlineData("rc call to 'operations/list' failed: directory not found")]
    [InlineData("No response within 10s.")]
    public void An_ordinary_connection_or_authentication_failure_is_not_a_host_key_failure(string text) =>
        Assert.Null(HostKeyFailure.Classify(text));

    [Theory]
    // "known" inside a host name, inside "unknown", and in a path -- none of them is a refusal.
    [InlineData("couldn't connect SSH: dial tcp: lookup knownhosts.example.net: no such host")]
    [InlineData("couldn't connect SSH: dial tcp: lookup unknownhost.example.net: no such host")]
    [InlineData("rc call to 'operations/list' failed: unknown error")]
    [InlineData(@"failed to read private key file: open C:\Users\someone\known_hosts_backup\id: The system cannot find the file specified.")]
    // The words of the refusal without the wrapper that makes it one.
    [InlineData("the file said knownhosts: key is unknown but this is not an ssh handshake")]
    // The "cannot find the file" text from some other open, not the known_hosts one.
    [InlineData(@"couldn't parse key_file: open C:\Users\someone\.ssh\id_ed25519: The system cannot find the file specified.")]
    // Not one of ADR-025's three cases; falls through to the ordinary failure text.
    [InlineData("couldn't connect SSH: ssh: handshake failed: knownhosts: key is revoked")]
    [InlineData(@"couldn't parse known_hosts_file: knownhosts: C:\Users\someone\.ssh\known_hosts:3: invalid line")]
    public void A_message_that_merely_contains_known_is_not_misclassified(string text) =>
        Assert.Null(HostKeyFailure.Classify(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_to_classify_is_null(string? text) => Assert.Null(HostKeyFailure.Classify(text));

    [Fact]
    public void The_three_user_facing_sentences_are_the_ones_ADR_025_specifies()
    {
        Assert.Equal(
            "Host key not verified: connect once through this host's Terminal profile, then Bosun will mount it.",
            HostKeyFailure.Describe(HostKeyFailureKind.UnknownKey));
        Assert.Equal(
            "Host key does not match known_hosts. Check the server; if its key really changed, remove the old line from known_hosts.",
            HostKeyFailure.Describe(HostKeyFailureKind.ChangedKey));
        Assert.Equal(
            @"No known_hosts file at %USERPROFILE%\.ssh\known_hosts, so no host can be verified.",
            HostKeyFailure.Describe(HostKeyFailureKind.KnownHostsMissing));
    }

    [Theory]
    [InlineData(HostKeyFailureKind.UnknownKey)]
    [InlineData(HostKeyFailureKind.ChangedKey)]
    [InlineData(HostKeyFailureKind.KnownHostsMissing)]
    public void The_user_facing_text_has_no_profile_path_key_material_or_address(HostKeyFailureKind kind)
    {
        var text = HostKeyFailure.Describe(kind);

        Assert.DoesNotContain(@"\Users\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ssh-ed25519", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ssh-rsa", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ecdsa-sha2", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b\d{1,3}(\.\d{1,3}){3}\b"), text);
        Assert.DoesNotContain("unreachable", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_kind_has_a_distinct_sentence()
    {
        var texts = Enum.GetValues<HostKeyFailureKind>().Select(HostKeyFailure.Describe).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
    }
}
