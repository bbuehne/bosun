using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bosun.Rclone;
using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Real-<c>rclone-rcd</c> integration coverage that two independent branches each contributed:
/// (1) <see cref="Correct_Basic_auth_succeeds_no_auth_and_wrong_password_are_both_rejected"/>,
/// raw-HTTP proof of the exact defect bs-ard fixes and the exact fix -- correct Basic auth
/// succeeds, no auth is rejected, a wrong password is rejected -- against rclone v1.75.0.
/// Deliberately bypasses <see cref="RcloneClient"/> entirely and talks to the rc HTTP API with
/// plain <see cref="HttpClient"/> calls, so this test cannot be fooled by a bug in
/// <see cref="RcloneClient"/> itself -- it proves what the WIRE actually does. (2) the two
/// bs-ehx tests below (<c>ConfigCreate_then_ConfigGet_roundtrips...</c> and
/// <c>ListMountsAsync_returns_an_empty_list...</c>), ported forward across the same bs-ard
/// change to run through the real, now-authenticated <see cref="RcloneClient"/> instead of the
/// <c>--rc-no-auth</c> bypass they originally used -- see their own remarks for what changed.
/// </summary>
/// <remarks>
/// Excluded from the default suite (CLAUDE.md worktree-safety rules). Run deliberately via
/// <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>. Every rcd instance in
/// this file is pointed at a throwaway <c>--config</c> file inside <see cref="Path.GetTempPath"/>,
/// created fresh per test and deleted in a <c>finally</c> block -- never the real
/// <c>%APPDATA%\rclone\rclone.conf</c>. <c>mount/mount</c> is never called anywhere in this file
/// (WinFsp is not installed here, and only <c>IMountSupervisor</c> may call it per the
/// architectural boundary on <see cref="IRcloneClient"/>); <c>mount/listmounts</c> is used as a
/// read-only, auth-requiring probe instead.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RcloneRcRealBinaryIntegrationTests
{
    private const int TestPort = 59574;

    [Fact]
    public async Task Correct_Basic_auth_succeeds_no_auth_and_wrong_password_are_both_rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bosun-rclone-rc-auth-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "rclone.conf");

        var credential = RcloneRcCredential.CreateRandom();
        var launcher = new Win32RcloneProcessLauncher();
        var baseAddress = new Uri($"http://127.0.0.1:{TestPort}/");

        var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = RcloneTestBinary.ResolveExecutablePath(),
            Arguments = ["rcd", "--rc-addr", $"127.0.0.1:{TestPort}", "--config", configPath],
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["RCLONE_RC_USER"] = credential.UserName,
                ["RCLONE_RC_PASS"] = credential.Password,
            },
        });

        try
        {
            // Readiness probe uses the correct credential, not no-auth: verified below (and
            // documented on RcloneClient/IRcloneClient.GetVersionAsync) that once rc auth is
            // CONFIGURED (RCLONE_RC_USER/RCLONE_RC_PASS set, exactly what Bosun always does),
            // rclone v1.75.0 enforces Basic auth on core/version too, despite rclone's own docs
            // saying "Authentication is not required for this call" -- that claim only holds when
            // no rc auth is configured at all. An unauthenticated readiness probe would therefore
            // never see anything but 401 and always time out.
            using var authedProbe = new HttpClient { BaseAddress = baseAddress };
            authedProbe.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credential.ToBasicAuthHeaderValue());
            await WaitUntilRcdIsRespondingAsync(authedProbe);

            // 1. Correct Basic auth -> 200, matching the exact manual verification in the bs-ard
            // brief ("correct Basic auth | 200 {"mountPoints": []}").
            using (var authedResponse = await PostAsync(baseAddress, "mount/listmounts", credential.UserName, credential.Password))
            {
                var body = await authedResponse.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, authedResponse.StatusCode);
                Assert.Contains("mountPoints", body);
            }

            // 2. No auth -> 401 (this is the bs-ard defect: EVERY endpoint but core/version used
            // to fail this way against Bosun's own real calls, not just a hand-crafted request).
            using (var unauthedResponse = await PostAsync(baseAddress, "mount/listmounts", user: null, pass: null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthedResponse.StatusCode);
            }

            // 3. Wrong password -> 401, not a silent success.
            using (var wrongPasswordResponse = await PostAsync(baseAddress, "mount/listmounts", credential.UserName, "definitely-the-wrong-password"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);
            }
        }
        finally
        {
            handle.Kill();
            handle.Dispose();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Ported forward from the pre-bs-ard version of this file (bs-ehx): verifies <c>config/get</c>'s
    /// response shape against a REAL <c>rclone rcd</c>, closing the one gap E3 flagged as inferred
    /// rather than verified (see the remarks on <see cref="IRcloneClient.GetConfigAsync"/> --
    /// inferred from <c>config/dump</c>'s documented shape, no worked example existed for
    /// <c>config/get</c> itself). Now runs against an AUTHENTICATED rcd (bs-ard changed every
    /// endpoint but <c>core/version</c> to require Basic auth), using the same random-credential +
    /// environment-variable launch pattern as
    /// <see cref="Correct_Basic_auth_succeeds_no_auth_and_wrong_password_are_both_rejected"/> above,
    /// and the real <see cref="RcloneClient"/> (which attaches that credential's Basic auth header
    /// to every request it sends) rather than the old <c>--rc-no-auth</c> bypass.
    /// </summary>
    [Fact]
    public async Task ConfigCreate_then_ConfigGet_roundtrips_the_sftp_parameter_names_against_a_real_rcd()
    {
        await RunAgainstAuthenticatedRcdAsync(async client =>
        {
            var parameters = new Dictionary<string, string>
            {
                ["host"] = "nas.example.internal",
                ["port"] = "22",
                ["user"] = "barry",
                ["key_file"] = @"C:\Users\barry\.ssh\id_ed25519",
                ["ask_password"] = "false",
            };

            await client.CreateConfigAsync("bosun-ehx-verify", "sftp", parameters, CancellationToken.None);

            var result = await client.GetConfigAsync("bosun-ehx-verify", CancellationToken.None);

            Assert.NotNull(result);
            // Real rclone v1.75.0's config/get response is confirmed to be exactly the flat
            // "key: value" object E3 inferred from config/dump's documented shape (plus a "type"
            // key rclone adds itself) -- not wrapped in an envelope, not nested. Authentication
            // (bs-ard) does not change this response shape at all, only whether the call is
            // accepted in the first place -- see the class-level report on what was actually
            // observed.
            Assert.Equal("nas.example.internal", result!["host"]);
            Assert.Equal("22", result["port"]);
            Assert.Equal("barry", result["user"]);
            Assert.Equal(@"C:\Users\barry\.ssh\id_ed25519", result["key_file"]);
            Assert.Equal("false", result["ask_password"]);
            Assert.Equal("sftp", result["type"]);
        });
    }

    /// <summary>
    /// Ported forward from the pre-bs-ard version of this file (bs-ehx): proves the empty-list
    /// shape <c>mount/listmounts</c> returns when rclone genuinely has nothing mounted --
    /// independent of ever calling <c>mount/mount</c> (never done anywhere in this file: WinFsp is
    /// not installed here, and only <c>IMountSupervisor</c> may call <c>mount/mount</c> per the
    /// architectural boundary on <see cref="IRcloneClient"/>). Now runs against an authenticated
    /// rcd -- see the remarks on the sibling <c>ConfigCreate_then_ConfigGet</c> test above for why.
    /// </summary>
    [Fact]
    public async Task ListMountsAsync_returns_an_empty_list_on_a_clean_rcd_with_nothing_mounted()
    {
        await RunAgainstAuthenticatedRcdAsync(async client =>
        {
            var result = await client.ListMountsAsync(CancellationToken.None);

            Assert.Empty(result);
        });
    }

    /// <summary>
    /// Starts a real, AUTHENTICATED <c>rclone rcd</c> bound to loopback with a throwaway
    /// <c>--config</c>, following exactly the same setup as
    /// <see cref="Correct_Basic_auth_succeeds_no_auth_and_wrong_password_are_both_rejected"/>:
    /// a fresh random <see cref="RcloneRcCredential"/>, passed to the child via
    /// <c>RCLONE_RC_USER</c>/<c>RCLONE_RC_PASS</c> environment variables (never command-line
    /// flags -- see <see cref="RcloneRcCredential"/>'s remarks), and the same credential passed
    /// into <see cref="RcloneClient"/>'s constructor so every call it makes, including the
    /// readiness probe, carries the matching Basic auth header. Shuts the process back down --
    /// always, even if <paramref name="body"/> throws.
    /// </summary>
    private static async Task RunAgainstAuthenticatedRcdAsync(Func<RcloneClient, Task> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bosun-rclone-ehx-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "rclone.conf");

        var credential = RcloneRcCredential.CreateRandom();
        var launcher = new Win32RcloneProcessLauncher();
        IRcloneProcessHandle? handle = null;

        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}/") };
            var client = new RcloneClient(httpClient, credential);

            handle = launcher.Start(new RcloneProcessStartInfo
            {
                ExecutablePath = RcloneTestBinary.ResolveExecutablePath(),
                Arguments = ["rcd", "--rc-addr", $"127.0.0.1:{TestPort}", "--config", configPath],
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["RCLONE_RC_USER"] = credential.UserName,
                    ["RCLONE_RC_PASS"] = credential.Password,
                },
            });

            await WaitUntilHealthyAsync(client, TimeSpan.FromSeconds(20));

            await body(client);
        }
        finally
        {
            handle?.Kill();
            handle?.Dispose();

            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static async Task WaitUntilHealthyAsync(RcloneClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.GetVersionAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lastFailure = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new TimeoutException(
            $"rclone rcd on port {TestPort} did not become healthy within {timeout}.", lastFailure);
    }

    private static async Task<HttpResponseMessage> PostAsync(Uri baseAddress, string endpoint, string? user, string? pass)
    {
        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        if (user is not null && pass is not null)
        {
            var headerValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", headerValue);
        }

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await httpClient.PostAsync(endpoint, content);
    }

    /// <summary>Polls <c>core/version</c> (with the correct credential -- see the caller's
    /// remarks on why an unauthenticated probe would never succeed once rc auth is configured)
    /// until it responds, proving the process is up before the auth-specific assertions run.
    /// Real wall-clock waiting is acceptable here -- this is a marked integration test against a
    /// real process, not part of the default suite's deterministic-time contract.</summary>
    private static async Task WaitUntilRcdIsRespondingAsync(HttpClient httpClient)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync("core/version", content);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException("rclone rcd did not respond to core/version within 15s.", lastError);
    }
}
