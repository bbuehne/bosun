using Bosun.Diagnostics;
using Bosun.Rclone;

namespace Bosun.Tests.Diagnostics;

/// <summary>
/// The redaction rules on their own. The bundle-level guarantee (nothing planted survives in any
/// zip entry) is in <see cref="DiagnosticsBundleBuilderTests"/>.
/// </summary>
public sealed class DiagnosticsRedactorTests
{
    private const string User = "planted-user-7f3a";
    private const string Password = "PlantedPw+/Zq9=Secret";

    private static DiagnosticsRedactor WithPlantedCredential() => new(new RcloneRcCredential(User, Password));

    [Fact]
    public void Live_password_is_replaced_wherever_it_appears()
    {
        var redactor = WithPlantedCredential();

        var result = redactor.Redact($"2026-10-01 [ERR] rc call failed using {Password} for something");

        Assert.DoesNotContain(Password, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Live_password_is_replaced_in_its_json_escaped_form()
    {
        // System.Text.Json writes '+' as + by default, so a literal match on the raw password
        // would miss it in any serialised document.
        var redactor = WithPlantedCredential();
        var jsonForm = System.Text.Json.JsonEncodedText.Encode(Password).ToString();
        Assert.NotEqual(Password, jsonForm);

        Assert.DoesNotContain(jsonForm, redactor.Redact($"{{\"commandLine\":\"x {jsonForm} y\"}}"));
    }

    [Fact]
    public void Live_basic_auth_header_value_and_user_password_pair_are_replaced()
    {
        var redactor = WithPlantedCredential();
        var credential = new RcloneRcCredential(User, Password);

        var result = redactor.Redact(
            $"Authorization: Basic {credential.ToBasicAuthHeaderValue()}{Environment.NewLine}curl http://{User}:{Password}@127.0.0.1:5572/");

        Assert.DoesNotContain(credential.ToBasicAuthHeaderValue(), result);
        Assert.DoesNotContain(Password, result);
        Assert.DoesNotContain(User, result);
    }

    [Fact]
    public void A_non_default_user_name_is_replaced_everywhere()
    {
        Assert.DoesNotContain(User, WithPlantedCredential().Redact($"user {User} connected"));
    }

    [Fact]
    public void The_default_user_name_is_kept_where_it_is_not_a_credential_because_it_is_the_product_name()
    {
        // "bosun" is the fixed, non-secret user name and also the name of the product. Scrubbing it
        // everywhere would turn "bosun.json" and "bosun-diagnostics" into "***".
        var redactor = new DiagnosticsRedactor(RcloneRcCredential.CreateRandom());

        var result = redactor.Redact(@"wrote bosun-diagnostics-1.zip next to bosun.json");

        Assert.Equal(@"wrote bosun-diagnostics-1.zip next to bosun.json", result);
    }

    [Fact]
    public void The_default_user_name_is_removed_where_it_is_a_credential()
    {
        var credential = RcloneRcCredential.CreateRandom();
        var redactor = new DiagnosticsRedactor(credential);

        var result = redactor.Redact(
            $"rclone rcd --rc-user={credential.UserName} --rc-pass {credential.Password}{Environment.NewLine}" +
            $"RCLONE_RC_USER={credential.UserName}{Environment.NewLine}" +
            $"seen {credential.UserName}:{credential.Password}");

        Assert.DoesNotContain(credential.Password, result);
        Assert.DoesNotContain($"{credential.UserName}:", result);
        Assert.DoesNotContain($"--rc-user={credential.UserName}", result);
        Assert.DoesNotContain($"RCLONE_RC_USER={credential.UserName}", result);
    }

    [Theory]
    [InlineData("rclone.exe rcd --rc-pass=hunter2 --rc-addr 127.0.0.1:5572", "hunter2", "--rc-pass=***")]
    [InlineData("rclone.exe rcd --rc-pass hunter2 --rc-addr 127.0.0.1:5572", "hunter2", "--rc-pass ***")]
    [InlineData("rclone.exe rcd --rc-pass \"hunter two\" --rc-addr 127.0.0.1:5572", "hunter", "--rc-pass ***")]
    [InlineData("rclone.exe rcd --rc-user=orphan --rc-addr 127.0.0.1:5572", "orphan", "--rc-user=***")]
    [InlineData("rclone.exe rcd --rc-user orphan --rc-addr 127.0.0.1:5572", "orphan", "--rc-user ***")]
    [InlineData("rclone.exe rcd --RC-PASS=hunter2", "hunter2", "--RC-PASS=***")]
    [InlineData("rclone.exe rcd --rc-htpasswd /x/.htpasswd-secret", "htpasswd-secret", "--rc-htpasswd ***")]
    [InlineData("rclone.exe mount --sftp-pass hunter2 r: Z:", "hunter2", "--sftp-pass ***")]
    public void Rc_credential_flags_are_redacted_whatever_the_value_and_spelling(string input, string secret, string expected)
    {
        // No live credential: this is the pattern layer alone, which is what catches a credential
        // belonging to a previous Bosun instance (the orphaned rcd of 2026-10-01).
        var result = new DiagnosticsRedactor(null).Redact(input);

        Assert.DoesNotContain(secret, result);
        Assert.Contains(expected, result);
        Assert.Contains("rclone.exe", result);
    }

    [Fact]
    public void A_flag_followed_by_another_flag_does_not_swallow_it()
    {
        var result = new DiagnosticsRedactor(null).Redact("rclone rcd --rc-pass --rc-addr 127.0.0.1:5572");

        Assert.Contains("--rc-addr 127.0.0.1:5572", result);
    }

    [Theory]
    [InlineData("RCLONE_RC_PASS=hunter2")]
    [InlineData("set RCLONE_RC_PASS=hunter2")]
    [InlineData("RCLONE_RC_PASS: hunter2")]
    [InlineData("\"RCLONE_RC_PASS\": \"hunter2\"")]
    public void Rc_environment_assignments_are_redacted(string input)
    {
        var result = new DiagnosticsRedactor(null).Redact(input);

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("RCLONE_RC_PASS", result);
    }

    [Fact]
    public void Authorization_headers_and_url_userinfo_are_redacted_without_a_live_credential()
    {
        var result = new DiagnosticsRedactor(null).Redact(
            "Authorization: Basic Ym9zdW46aHVudGVyMg==" + Environment.NewLine +
            "GET http://someone:hunter2@127.0.0.1:5572/core/version");

        Assert.DoesNotContain("Ym9zdW46aHVudGVyMg", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("someone", result);
        Assert.Contains("127.0.0.1:5572/core/version", result);
    }

    [Fact]
    public void Private_key_blocks_are_removed_whether_multi_line_or_flattened_into_one_line()
    {
        var multiLine =
            "before" + Environment.NewLine +
            "-----BEGIN OPENSSH PRIVATE KEY-----" + Environment.NewLine +
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmU=" + Environment.NewLine +
            "-----END OPENSSH PRIVATE KEY-----" + Environment.NewLine +
            "after";
        var oneLine = "{\"key\":\"-----BEGIN RSA PRIVATE KEY-----\\nMIIEpAIBAAKCAQEA\\n-----END RSA PRIVATE KEY-----\\n\"}";
        var redactor = new DiagnosticsRedactor(null);

        var first = redactor.Redact(multiLine);
        var second = redactor.Redact(oneLine);

        Assert.DoesNotContain("b3BlbnNzaC1r", first);
        Assert.Contains("before", first);
        Assert.Contains("after", first);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEA", second);
    }

    [Fact]
    public void Private_key_blocks_are_removed_from_a_streamed_log()
    {
        var log =
            "line one" + Environment.NewLine +
            "-----BEGIN PRIVATE KEY-----" + Environment.NewLine +
            "SECRETKEYBODY" + Environment.NewLine +
            "-----END PRIVATE KEY-----" + Environment.NewLine +
            "line two" + Environment.NewLine;
        using var reader = new StringReader(log);
        using var writer = new StringWriter();

        new DiagnosticsRedactor(null).Redact(reader, writer);

        Assert.DoesNotContain("SECRETKEYBODY", writer.ToString());
        Assert.Contains("line one", writer.ToString());
        Assert.Contains("line two", writer.ToString());
    }

    [Fact]
    public void Text_without_secrets_and_its_line_endings_pass_through_unchanged()
    {
        var text = "a\r\nb\nc without a newline at the end";

        Assert.Equal(text, new DiagnosticsRedactor(RcloneRcCredential.CreateRandom()).Redact(text));
    }

    [Fact]
    public void Toml_keeps_hosts_users_and_identity_files_but_masks_secret_looking_keys()
    {
        const string toml = """"
            [global]
            rclone_rc_port = 5572

            [hosts.example]
            hostname = "files.example.net"
            user = "someuser"
            identity_file = "~/.ssh/id_example"
            password = "hunter2"
            key_passphrase = 'hunter3'
            api_token = "hunter4"
            Secret = "hunter5"
            key_material = """
            -----multi
            hunter6
            """
            after = "kept"
            # old_password = "hunter7"
            """";

        var result = WithPlantedCredential().RedactToml(toml);

        foreach (var secret in new[] { "hunter2", "hunter3", "hunter4", "hunter5", "hunter6", "hunter7" })
        {
            Assert.DoesNotContain(secret, result);
        }

        Assert.Contains("files.example.net", result);
        Assert.Contains("someuser", result);
        Assert.Contains("~/.ssh/id_example", result);
        Assert.Contains("rclone_rc_port = 5572", result);
        Assert.Contains("after = \"kept\"", result);
        Assert.Contains("password = \"***\"", result);
    }

    [Fact]
    public void Toml_also_gets_the_live_credential_removed_from_values_that_are_not_secret_looking()
    {
        var result = WithPlantedCredential().RedactToml($"note = \"pasted {Password} here\"");

        Assert.DoesNotContain(Password, result);
    }
}
