using Bosun.Rclone;
using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone;

/// <summary>
/// End-to-end against a REAL <c>rclone rcd</c> child process and a REAL <see cref="RcloneClient"/>
/// HTTP call -- excluded from the default suite (CLAUDE.md worktree-safety rules). rclone is NOT
/// installed on the maintainer's machine as of this epic landing, so this test could not be run
/// or verified during E3's implementation; it is written so the maintainer can run it once rclone
/// is installed, via <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>.
/// Uses a loopback port unlikely to collide with a real Bosun instance's own rcd.
/// </summary>
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
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}/") };
            var client = new RcloneClient(httpClient);
            var options = new RcloneProcessServiceOptions
            {
                RcloneRcPort = TestPort,
                RcloneConfigPath = configPath,
            };
            var service = new RcloneProcessService(
                new Win32RcloneProcessLauncher(),
                client,
                options,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RcloneProcessService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(RcloneProcessStatus.Healthy, service.Status);

                var version = await client.GetVersionAsync(CancellationToken.None);
                Assert.False(string.IsNullOrEmpty(version.Version));
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
