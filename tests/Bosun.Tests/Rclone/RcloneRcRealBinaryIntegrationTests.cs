using Bosun.Rclone;
using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Verifies bs-ehx: two more <c>rclone rc</c> endpoints against a REAL <c>rclone rcd</c> child
/// process -- excluded from the default suite (CLAUDE.md worktree-safety rules). Closes the one
/// gap E3 flagged as inferred rather than verified: <c>config/get</c>'s response shape (see the
/// remarks on <see cref="IRcloneClient.GetConfigAsync"/>), plus <c>mount/listmounts</c>' empty-list
/// shape that E5's reconciliation and bs-ka9's Fs-aware adoption both depend on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safety.</b> Every rcd instance here is pointed at a throwaway <c>--config</c> file inside
/// <see cref="Path.GetTempPath"/>, created fresh per test and deleted in a <c>finally</c> block --
/// never the real <c>%APPDATA%\rclone\rclone.conf</c>. <c>mount/mount</c> and <c>mount/unmount</c>
/// are never called (they need WinFsp, not installed here, and only <c>IMountSupervisor</c> may
/// call them per the architectural boundary documented on <see cref="IRcloneClient"/>).
/// </para>
/// <para>
/// <b>Deliberately bypasses <see cref="RcloneProcessService"/>.</b> This class starts the rcd
/// process directly via <see cref="Win32RcloneProcessLauncher"/> (the same launcher
/// <see cref="RcloneProcessService"/> uses in production) with an extra <c>--rc-no-auth</c> flag
/// that <see cref="RcloneProcessService.BuildStartInfo"/> does NOT currently pass. That is a
/// deliberate scope boundary, not an oversight -- see the bs-ehx delivery report: rclone v1.75.0
/// empirically returns <c>403 Forbidden</c> ("authentication must be set up on the rc server...
/// or the --rc-no-auth flag must be in use") for every rc call in this file EXCEPT
/// <c>core/version</c> when that flag is absent, which contradicts the citation in
/// <see cref="RcloneProcessService.BuildStartInfo"/>'s remarks. Fixing that citation/flag is a
/// production-code, security-relevant decision (auth vs. <c>--rc-no-auth</c>) outside bs-ehx's
/// "verify parameter shapes" scope, so it is reported as discovered work rather than silently
/// patched here. This test adds the flag itself, locally, purely so the shapes below can be
/// exercised against a real server at all.
/// </para>
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RcloneRcRealBinaryIntegrationTests
{
    private const int TestPort = 59574;

    [Fact]
    public async Task ConfigCreate_then_ConfigGet_roundtrips_the_sftp_parameter_names_against_a_real_rcd()
    {
        await RunAgainstRealRcdAsync(async client =>
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
            // key rclone adds itself) -- not wrapped in an envelope, not nested. See the class
            // remarks and the bs-ehx delivery report for the literal observed JSON.
            Assert.Equal("nas.example.internal", result!["host"]);
            Assert.Equal("22", result["port"]);
            Assert.Equal("barry", result["user"]);
            Assert.Equal(@"C:\Users\barry\.ssh\id_ed25519", result["key_file"]);
            Assert.Equal("false", result["ask_password"]);
            Assert.Equal("sftp", result["type"]);
        });
    }

    [Fact]
    public async Task ListMountsAsync_returns_an_empty_list_on_a_clean_rcd_with_nothing_mounted()
    {
        await RunAgainstRealRcdAsync(async client =>
        {
            // No mount/mount call anywhere in this file: WinFsp is not installed, and only
            // IMountSupervisor may call mount/mount per the architectural boundary on
            // IRcloneClient. This proves the *shape* mount/listmounts returns when rclone
            // genuinely has nothing mounted, independent of ever mounting anything.
            var result = await client.ListMountsAsync(CancellationToken.None);

            Assert.Empty(result);
        });
    }

    /// <summary>
    /// Starts a real <c>rclone rcd</c> bound to loopback with a throwaway <c>--config</c>,
    /// real <see cref="RcloneClient"/> HTTP calls, and shuts it back down -- always, even if
    /// <paramref name="body"/> throws.
    /// </summary>
    private static async Task RunAgainstRealRcdAsync(Func<RcloneClient, Task> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bosun-rclone-ehx-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "rclone.conf");

        var launcher = new Win32RcloneProcessLauncher();
        IRcloneProcessHandle? handle = null;

        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}/") };
            var client = new RcloneClient(httpClient);

            handle = launcher.Start(new RcloneProcessStartInfo
            {
                ExecutablePath = ResolveRcloneExecutablePath(),
                Arguments =
                [
                    "rcd",
                    "--rc-addr",
                    $"127.0.0.1:{TestPort}",
                    "--config",
                    configPath,
                    // See the class remarks: v1.75.0 rejects every endpoint below with 403 without
                    // this flag. Local to this test, deliberately not added to production code.
                    "--rc-no-auth",
                ],
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

    /// <summary>Resolves the same way <see cref="RcloneProcessServiceOptions.ExecutablePath"/>'s
    /// default ("rclone", resolved via PATH) is meant to in production, but falls back to the
    /// known install location if PATH resolution fails in this shell -- see the bs-ehx delivery
    /// report for why that fallback exists (PATH was updated by the installer but not yet visible
    /// to every already-running shell in this environment).</summary>
    private static string ResolveRcloneExecutablePath()
    {
        var explicitPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rclone", "rclone.exe");

        return File.Exists(explicitPath) ? explicitPath : "rclone";
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
}
