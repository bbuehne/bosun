using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bosun.Rclone;
using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Raw-HTTP proof, against a REAL rclone rcd binary, of the exact defect bs-ard fixes and the
/// exact fix: correct Basic auth succeeds, no auth is rejected, and a wrong password is rejected
/// -- the same three outcomes verified manually against rclone v1.75.0 before this fix was
/// written (see the bs-ard brief). Deliberately bypasses <see cref="RcloneClient"/> entirely and
/// talks to the rc HTTP API with plain <see cref="HttpClient"/> calls, so this test cannot be
/// fooled by a bug in <see cref="RcloneClient"/> itself -- it proves what the WIRE actually does.
/// </summary>
/// <remarks>
/// Excluded from the default suite (CLAUDE.md worktree-safety rules). Run deliberately via
/// <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>. Uses
/// <c>mount/listmounts</c> as the auth-requiring probe -- read-only, safe, and never calls
/// <c>mount/mount</c> (WinFsp is not required to be installed for this test to run).
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
