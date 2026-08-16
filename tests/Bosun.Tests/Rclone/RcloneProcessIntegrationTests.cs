using Bosun.Rclone;
using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone;

/// <summary>
/// End-to-end against a REAL <c>rclone rcd</c> child process and a REAL <see cref="RcloneClient"/>
/// HTTP call -- excluded from the default suite (CLAUDE.md worktree-safety rules). Run
/// deliberately via <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>.
/// Uses loopback ports unlikely to collide with a real Bosun instance's own rcd.
/// </summary>
/// <remarks>
/// bs-ard: extended to prove the whole chain -- <see cref="RcloneProcessService"/> launching rcd
/// with a random <see cref="RcloneRcCredential"/> in its environment, and <see cref="RcloneClient"/>
/// authenticating with the SAME credential -- actually authenticates against a real rclone
/// binary, not just that SOME call responds. (Calling only <c>core/version</c> would not have
/// proven this: against a real v1.75.0 binary with rc auth configured, that endpoint ALSO
/// requires auth -- see the caveat on <see cref="IRcloneClient.GetVersionAsync"/> -- so it alone
/// cannot distinguish "auth wiring works" from "auth wiring is silently broken but this one
/// endpoint happens to tolerate it".) <c>mount/listmounts</c> is used as a second, unambiguously
/// auth-requiring probe because it is read-only and safe (WinFsp is not required to call it, and
/// it never creates a mount) -- never call <c>mount/mount</c> from this suite (CLAUDE.md
/// worktree-safety rules).
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RcloneProcessIntegrationTests
{
    private const int TestPort = 59572;

    [Fact]
    public async Task RcloneProcessService_starts_a_real_rcd_and_becomes_Healthy_via_a_real_core_version_call()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bosun-rclone-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "rclone.conf");

        try
        {
            var credential = RcloneRcCredential.CreateRandom();
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}/") };
            var client = new RcloneClient(httpClient, credential);
            var options = new RcloneProcessServiceOptions
            {
                ExecutablePath = RcloneTestBinary.ResolveExecutablePath(),
                RcloneRcPort = TestPort,
                RcloneConfigPath = configPath,
            };
            var service = new RcloneProcessService(
                new Win32RcloneProcessLauncher(),
                client,
                options,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RcloneProcessService>.Instance,
                credential);

            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(RcloneProcessStatus.Healthy, service.Status);

                // This alone is not conclusive proof the auth wiring is correct -- see the class
                // remarks on why core/version cannot distinguish "working" from "silently broken".
                var version = await client.GetVersionAsync(CancellationToken.None);
                Assert.False(string.IsNullOrEmpty(version.Version));

                // mount/listmounts unambiguously requires auth once rc auth is configured
                // (verified against real rclone v1.75.0: 401 without credentials, see
                // RcloneRcRealBinaryIntegrationTests). A successful call here proves
                // RcloneProcessService's RCLONE_RC_USER/RCLONE_RC_PASS environment variables and
                // RcloneClient's Authorization header actually agree with each other end to end.
                var mounts = await client.ListMountsAsync(CancellationToken.None);
                Assert.Empty(mounts);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
