using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bosun.Rclone;

namespace Bosun.Diagnostics;

/// <summary>
/// The one redaction pass every text in a diagnostics bundle goes through (bs-ds3, ADR-020 §6):
/// summary, JSON, version, config and log copies. <see cref="DiagnosticsBundleBuilder"/> has no way
/// to write a text entry that skips it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it removes.</b> Two layers, because neither alone is enough:
/// </para>
/// <list type="number">
/// <item><b>The live credential, by value.</b> The password is replaced wherever it appears, in its
/// raw, JSON-escaped (<c>System.Text.Json</c> writes <c>+</c> as <c>+</c>), URL-escaped and
/// <c>user:password</c> / Basic-auth base64 forms. This catches the credential in places no pattern
/// would think to look, for example a log line that happens to print it.</item>
/// <item><b>Shapes that carry secrets, whatever the value.</b> <c>--rc-user</c>/<c>--rc-pass</c> (and
/// any flag ending in <c>pass</c>, <c>password</c>, <c>secret</c>, <c>token</c> or <c>key-pem</c>) in
/// both the <c>=value</c> and the <c>value</c> spelling, <c>RCLONE_RC_USER</c>/<c>RCLONE_RC_PASS</c>
/// assignments, <c>Authorization: Basic</c> headers, <c>user:password@</c> URL userinfo, and PEM
/// private key blocks. This catches a credential from a previous Bosun instance, such as the one an
/// orphaned rcd was started with.</item>
/// </list>
/// <para>
/// <b>The user name.</b> <see cref="RcloneRcCredential.DefaultUserName"/> is "bosun": non-secret and
/// the product's own name. Replacing it everywhere would turn "bosun.json", "bosun-diagnostics" and
/// every log source into <c>***</c>. So it is removed where it is a credential (the flag, the
/// environment assignment, the <c>user:password</c> pair), and removed wholesale only when a
/// different user name is in use.
/// </para>
/// <para>
/// <b>Environment variables.</b> Never collected, so there is nothing to redact. The process
/// inspector reads image path and command line only.
/// </para>
/// </remarks>
public sealed class DiagnosticsRedactor
{
    public const string Mask = "***";

    private const string PrivateKeyMask = "***PRIVATE KEY REDACTED***";

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // --rc-pass=x, --rc-pass x, --rc-pass "x y", --sftp-key-file-pass=x. A value that itself starts
    // with "--" is the next flag, not a value.
    private static readonly Regex FlagValue = new(
        @"(?<flag>--(?:rc-(?:user|pass|htpasswd)|[a-z0-9-]*?(?:pass|password|secret|token|key-pem)))(?<sep>\s*=\s*|\s+)(?!--)(?<val>""[^""]*""|'[^']*'|[^\s""']+)",
        Options);

    // RCLONE_RC_USER=x, set RCLONE_RC_PASS=x, "RCLONE_RC_PASS": "x".
    private static readonly Regex EnvAssignment = new(
        @"(?<key>RCLONE_RC_(?:USER|PASS)\w*)(?<sep>[""']?\s*[=:]\s*)(?<val>""[^""]*""|'[^']*'|[^\s,;""']+)",
        Options);

    private static readonly Regex BasicAuthHeader = new(
        @"(?<key>Authorization[""']?\s*[:=]\s*[""']?Basic\s+)[A-Za-z0-9+/=_\-]+",
        Options);

    private static readonly Regex UrlUserInfo = new(
        @"(?<scheme>[a-z][a-z0-9+.\-]*://)[^/\s@:]+:[^/\s@]+@",
        Options);

    private static readonly Regex InlinePrivateKey = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        Options | RegexOptions.Singleline);

    // A TOML assignment whose key names a secret. The leading "#" lets a commented-out secret go
    // too: a comment is still text a person would paste into an issue.
    private static readonly Regex SecretTomlAssignment = new(
        @"^(?<lead>\s*#?\s*)(?<key>[A-Za-z0-9_.\-""']*(?:pass|password|secret|token|key_material)[A-Za-z0-9_.\-""']*)\s*=\s*(?<val>.*)$",
        Options);

    private readonly string[] _literals;

    /// <param name="credential">The live rc credential, or <see langword="null"/> when there is none
    /// (only the pattern layer then applies).</param>
    public DiagnosticsRedactor(RcloneRcCredential? credential)
    {
        var literals = new List<string>();
        if (credential is not null)
        {
            var user = credential.UserName;
            var password = credential.Password;

            // Longest first, so "user:password" goes before "password" alone leaves "user:***".
            literals.Add($"{user}:{password}");
            literals.Add(credential.ToBasicAuthHeaderValue());
            literals.Add(password);
            literals.Add(JsonEncodedText.Encode(password).ToString());
            literals.Add(Uri.EscapeDataString(password));

            if (!string.Equals(user, RcloneRcCredential.DefaultUserName, StringComparison.OrdinalIgnoreCase))
            {
                literals.Add(user);
            }
        }

        _literals = literals
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(l => l.Length)
            .ToArray();
    }

    /// <summary>Redacts a whole text, preserving its line endings.</summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new StringBuilder(text.Length);
        var inPem = false;
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;

            // Split the terminator off ("\n" or "\r\n") so patterns never see it.
            var contentEnd = newline < 0 ? end : newline;
            if (newline > start && text[newline - 1] == '\r')
            {
                contentEnd = newline - 1;
            }

            var line = text[start..contentEnd];
            var terminator = text[contentEnd..end];

            var redacted = RedactLine(line, ref inPem);
            if (redacted is not null)
            {
                result.Append(redacted).Append(terminator);
            }

            start = end;
        }

        return result.ToString();
    }

    /// <summary>Redacts a text stream line by line, so a multi-day log is never held in memory.
    /// Line endings are written as <see cref="TextWriter.NewLine"/>.</summary>
    public void Redact(TextReader reader, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var inPem = false;
        while (reader.ReadLine() is { } line)
        {
            var redacted = RedactLine(line, ref inPem);
            if (redacted is not null)
            {
                writer.WriteLine(redacted);
            }
        }
    }

    /// <summary>
    /// Redacts <c>hosts.toml</c> content. Host names, user names and identity-file paths stay: this
    /// is a personal tool and they are needed to diagnose it. Any assignment whose key looks secret
    /// (pass, password, secret, token, key_material) has its whole value, including a multi-line
    /// one, replaced with <c>***</c>. The general pass then runs over the result.
    /// </summary>
    public string RedactToml(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);

        var output = new StringBuilder(toml.Length);
        using var reader = new StringReader(toml);
        string? skipUntilDelimiter = null;
        var skipBracketDepth = 0;

        while (reader.ReadLine() is { } line)
        {
            if (skipUntilDelimiter is not null)
            {
                // Inside a multi-line secret value: drop lines until it closes.
                if (line.Contains(skipUntilDelimiter, StringComparison.Ordinal))
                {
                    skipUntilDelimiter = null;
                }

                continue;
            }

            if (skipBracketDepth > 0)
            {
                skipBracketDepth += BracketDelta(line);
                continue;
            }

            var match = SecretTomlAssignment.Match(line);
            if (!match.Success)
            {
                output.AppendLine(line);
                continue;
            }

            var value = match.Groups["val"].Value.TrimStart();
            var delimiter = value.StartsWith("\"\"\"", StringComparison.Ordinal) ? "\"\"\""
                : value.StartsWith("'''", StringComparison.Ordinal) ? "'''"
                : null;

            if (delimiter is not null && value.IndexOf(delimiter, delimiter.Length, StringComparison.Ordinal) < 0)
            {
                skipUntilDelimiter = delimiter;
            }
            else if (value.StartsWith('[') && BracketDelta(value) > 0)
            {
                skipBracketDepth = BracketDelta(value);
            }

            output.Append(match.Groups["lead"].Value)
                .Append(match.Groups["key"].Value)
                .Append(" = \"")
                .Append(Mask)
                .AppendLine("\"");
        }

        return Redact(output.ToString());
    }

    private static int BracketDelta(string text)
    {
        var delta = 0;
        foreach (var c in text)
        {
            if (c == '[')
            {
                delta++;
            }
            else if (c == ']')
            {
                delta--;
            }
        }

        return delta;
    }

    /// <summary>Returns the redacted line, or <see langword="null"/> for a line to drop (the inside
    /// of a PEM private key block).</summary>
    private string? RedactLine(string line, ref bool inPem)
    {
        if (inPem)
        {
            if (line.Contains("-----END ", StringComparison.Ordinal))
            {
                inPem = false;
            }

            return null;
        }

        if (line.Contains("-----BEGIN ", StringComparison.Ordinal)
            && line.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            if (InlinePrivateKey.IsMatch(line))
            {
                line = InlinePrivateKey.Replace(line, PrivateKeyMask);
            }
            else
            {
                // BEGIN with no END on this line: the key body follows on later lines.
                inPem = true;
                return PrivateKeyMask;
            }
        }

        // Literal values first: "user:password" must be replaced whole before the password alone.
        foreach (var literal in _literals)
        {
            line = line.Replace(literal, Mask, StringComparison.Ordinal);
        }

        line = FlagValue.Replace(line, m => $"{m.Groups["flag"].Value}{m.Groups["sep"].Value}{Mask}");
        line = EnvAssignment.Replace(line, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Mask}");
        line = BasicAuthHeader.Replace(line, m => $"{m.Groups["key"].Value}{Mask}");
        line = UrlUserInfo.Replace(line, m => $"{m.Groups["scheme"].Value}{Mask}@");
        return line;
    }
}
