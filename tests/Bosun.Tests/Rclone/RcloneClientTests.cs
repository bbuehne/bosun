using System.Net;
using System.Text.Json;
using Bosun.Rclone;
using Bosun.Tests.Rclone.Fakes;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Covers bs-tg9's <see cref="RcloneClient"/>. Every rc parameter name asserted here was
/// verified against rclone's current documentation (see the source URLs cited in
/// <see cref="IRcloneClient"/> and <see cref="RcloneClient"/>) rather than recalled from memory.
/// No real HTTP anywhere -- <see cref="FakeHttpMessageHandler"/> backs every
/// <see cref="HttpClient"/> (CLAUDE.md worktree-safety rules).
/// </summary>
public sealed class RcloneClientTests
{
    [Fact]
    public async Task GetVersionAsync_parses_the_version_field()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"version":"v1.71.2","os":"windows","arch":"amd64"}""");

        var result = await client.GetVersionAsync(CancellationToken.None);

        Assert.Equal("v1.71.2", result.Version);
        Assert.Equal("windows", result.Os);
        Assert.Equal("amd64", result.Arch);
        Assert.Equal("core/version", handler.Requests[0].Endpoint);
    }

    [Fact]
    public async Task CreateConfigAsync_sends_name_type_and_parameters_verbatim_in_the_JSON_body()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        await client.CreateConfigAsync(
            "bosun-example-nas",
            "sftp",
            new Dictionary<string, string>
            {
                ["host"] = "nas.example.internal",
                ["port"] = "22",
                ["user"] = "barry",
                ["key_file"] = @"C:\Users\barry\.ssh\id_ed25519",
                ["ask_password"] = "false",
            },
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("config/create", request.Endpoint);

        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("bosun-example-nas", body.GetProperty("name").GetString());
        Assert.Equal("sftp", body.GetProperty("type").GetString());
        var parameters = body.GetProperty("parameters");
        Assert.Equal("nas.example.internal", parameters.GetProperty("host").GetString());
        Assert.Equal("22", parameters.GetProperty("port").GetString());
        Assert.Equal("barry", parameters.GetProperty("user").GetString());
        Assert.Equal(@"C:\Users\barry\.ssh\id_ed25519", parameters.GetProperty("key_file").GetString());
        Assert.Equal("false", parameters.GetProperty("ask_password").GetString());
    }

    [Fact]
    public async Task GetConfigAsync_returns_the_flat_parameter_object_on_success()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"host":"nas.example.internal","port":"22","user":"barry","type":"sftp"}""");

        var result = await client.GetConfigAsync("bosun-example-nas", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("nas.example.internal", result!["host"]);
        Assert.Equal("22", result["port"]);
        Assert.Equal("config/get", handler.Requests[0].Endpoint);
        Assert.Equal("bosun-example-nas", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetConfigAsync_returns_null_when_the_rc_call_fails_rather_than_throwing()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, """{"error":"didn't find section in config file"}""");

        var result = await client.GetConfigAsync("bosun-missing", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task MountAsync_hardcodes_network_mode_true_regardless_of_anything_the_caller_supplies()
    {
        // Invariant I7: there is no RcloneMountRequest field that could set this to false --
        // this test asserts on the ACTUAL JSON body, not a helper's return value, per the E3 brief.
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"mountPoint":"N:"}""");

        await client.MountAsync(
            new RcloneMountRequest { Fs = "bosun-example-nas:/volume1/share", MountPoint = "N:", VfsCacheMode = "writes" },
            CancellationToken.None);

        var body = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.True(body.GetProperty("network_mode").GetBoolean());
    }

    [Theory]
    [InlineData(null, "writes")]
    [InlineData("", "writes")]
    [InlineData("off", "writes")]
    [InlineData("minimal", "writes")]
    [InlineData("writes", "writes")]
    [InlineData("full", "full")]
    [InlineData("WRITES", "WRITES")] // already at/above the floor (case-insensitive check) -- passed through as-is
    public async Task MountAsync_enforces_the_vfs_cache_mode_floor_at_the_point_the_body_is_built(
        string? requested, string expectedInBody)
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"mountPoint":"N:"}""");

        await client.MountAsync(
            new RcloneMountRequest { Fs = "bosun-example-nas:/volume1/share", MountPoint = "N:", VfsCacheMode = requested },
            CancellationToken.None);

        var body = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.Equal(expectedInBody, body.GetProperty("vfs_cache_mode").GetString());
    }

    [Fact]
    public async Task MountAsync_sends_fs_and_mountPoint_and_returns_the_actual_mount_point()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"mountPoint":"N:"}""");

        var result = await client.MountAsync(
            new RcloneMountRequest { Fs = "bosun-example-nas:/volume1/share", MountPoint = "*" },
            CancellationToken.None);

        Assert.Equal("N:", result.MountPoint);
        var body = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.Equal("bosun-example-nas:/volume1/share", body.GetProperty("fs").GetString());
        Assert.Equal("*", body.GetProperty("mountPoint").GetString());
        Assert.Equal("mount/mount", handler.Requests[0].Endpoint);
    }

    [Fact]
    public async Task UnmountAsync_sends_the_mount_point()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        await client.UnmountAsync("N:", CancellationToken.None);

        Assert.Equal("mount/unmount", handler.Requests[0].Endpoint);
        Assert.Equal("N:", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("mountPoint").GetString());
    }

    [Fact]
    public async Task ListMountsAsync_parses_Fs_MountPoint_and_MountedOn()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"mountPoints":[{"Fs":"bosun-example-nas:/volume1/share","MountPoint":"N:","MountedOn":"2026-08-15T10:00:00Z"}]}
            """);

        var result = await client.ListMountsAsync(CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal("bosun-example-nas:/volume1/share", entry.Fs);
        Assert.Equal("N:", entry.MountPoint);
        Assert.Equal(DateTimeOffset.Parse("2026-08-15T10:00:00Z"), entry.MountedOn);
        Assert.Equal("mount/listmounts", handler.Requests[0].Endpoint);
    }

    [Fact]
    public async Task ListMountsAsync_returns_empty_when_mountPoints_is_absent()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var result = await client.ListMountsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAsync_sends_fs_and_remote_without_a_recurse_flag_depth_one()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"list":[{"Path":"docs","Name":"docs","IsDir":true},{"Path":"readme.txt","Name":"readme.txt","IsDir":false,"Size":42}]}""");

        var result = await client.ListAsync("bosun-example-nas:", "", CancellationToken.None);

        Assert.Equal("operations/list", handler.Requests[0].Endpoint);
        var body = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.Equal("bosun-example-nas:", body.GetProperty("fs").GetString());
        Assert.Equal("", body.GetProperty("remote").GetString());
        Assert.False(body.TryGetProperty("opt", out _), "operations/list must not request recurse -- depth 1 is the deep-probe contract");

        Assert.Equal(2, result.Count);
        Assert.Equal("docs", result[0].Name);
        Assert.True(result[0].IsDir);
        Assert.Equal(42, result[1].Size);
    }

    [Fact]
    public async Task A_non_success_status_becomes_an_RcloneRcException_carrying_the_rc_error_message()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, """{"error":"remote 'bosun-example-nas' not found"}""");

        var ex = await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVersionAsync(CancellationToken.None));

        Assert.Equal("core/version", ex.Endpoint);
        Assert.Equal(500, ex.HttpStatusCode);
        Assert.Contains("remote 'bosun-example-nas' not found", ex.Message);
    }

    [Fact]
    public async Task A_response_that_is_not_a_JSON_object_becomes_an_RcloneRcException_not_a_crash()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, "not json at all");

        await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVersionAsync(CancellationToken.None));
    }

    // -- bs-ard: Basic auth on every request ------------------------------------------------

    [Fact]
    public async Task GetVersionAsync_sends_correct_Basic_auth_even_though_rclone_says_this_call_does_not_require_it()
    {
        // Uniformity over cleverness: RcloneClient does not special-case core/version just
        // because rclone's docs say it is the one endpoint that does not require auth.
        var credential = new RcloneRcCredential("bosun-test-user", "bosun-test-pass");
        var (client, handler) = CreateClient(credential);
        handler.EnqueueJson(HttpStatusCode.OK, """{"version":"v1.75.0"}""");

        await client.GetVersionAsync(CancellationToken.None);

        AssertBasicAuth(handler.Requests[0].Authorization, credential);
    }

    [Theory]
    [InlineData("core/version")]
    [InlineData("config/create")]
    [InlineData("config/get")]
    [InlineData("mount/mount")]
    [InlineData("mount/unmount")]
    [InlineData("mount/listmounts")]
    [InlineData("operations/list")]
    public async Task Every_endpoint_sends_the_same_Basic_auth_header(string endpoint)
    {
        var credential = new RcloneRcCredential("bosun-test-user", "bosun-test-pass");
        var (client, handler) = CreateClient(credential);
        handler.EnqueueJson(HttpStatusCode.OK, """{"version":"v1.75.0","mountPoint":"N:"}""");

        switch (endpoint)
        {
            case "core/version":
                await client.GetVersionAsync(CancellationToken.None);
                break;
            case "config/create":
                await client.CreateConfigAsync("bosun-example-nas", "sftp", new Dictionary<string, string>(), CancellationToken.None);
                break;
            case "config/get":
                await client.GetConfigAsync("bosun-example-nas", CancellationToken.None);
                break;
            case "mount/mount":
                await client.MountAsync(new RcloneMountRequest { Fs = "bosun-example-nas:/", MountPoint = "N:" }, CancellationToken.None);
                break;
            case "mount/unmount":
                await client.UnmountAsync("N:", CancellationToken.None);
                break;
            case "mount/listmounts":
                await client.ListMountsAsync(CancellationToken.None);
                break;
            case "operations/list":
                await client.ListAsync("bosun-example-nas:", "", CancellationToken.None);
                break;
        }

        Assert.Equal(endpoint, handler.Requests[0].Endpoint);
        AssertBasicAuth(handler.Requests[0].Authorization, credential);
    }

    [Fact]
    public void Constructor_rejects_a_null_credential_rather_than_silently_sending_unauthenticated_requests()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("http://127.0.0.1:5572/") };
        Assert.Throws<ArgumentNullException>(() => new RcloneClient(httpClient, credential: null!));
    }

    private static void AssertBasicAuth(System.Net.Http.Headers.AuthenticationHeaderValue? actual, RcloneRcCredential expected)
    {
        Assert.NotNull(actual);
        Assert.Equal("Basic", actual!.Scheme);
        Assert.Equal(expected.ToBasicAuthHeaderValue(), actual.Parameter);
    }

    private static (RcloneClient Client, FakeHttpMessageHandler Handler) CreateClient(RcloneRcCredential? credential = null)
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5572/") };
        return (new RcloneClient(httpClient, credential ?? new RcloneRcCredential("bosun-test-user", "bosun-test-pass")), handler);
    }
}
