namespace Bosun.Tests.Rclone;

/// <summary>
/// Shared executable-path resolution for the rclone integration suites
/// (<see cref="RcloneProcessIntegrationTests"/>, <c>RcloneRcRealBinaryIntegrationTests</c>).
/// rclone may not be on PATH in the shell these tests run in even when it is installed --
/// verified installed at <c>%LOCALAPPDATA%\rclone\rclone.exe</c> on the maintainer's machine as
/// of bs-ard, postdating whatever session's PATH a test runner inherited. Prefer that known
/// location when it exists; fall back to the bare <c>"rclone"</c> name (PATH-resolved) otherwise,
/// which lets these tests keep working unmodified if rclone is ever installed conventionally.
/// </summary>
internal static class RcloneTestBinary
{
    public static string ResolveExecutablePath()
    {
        var localAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rclone", "rclone.exe");

        return File.Exists(localAppDataPath) ? localAppDataPath : "rclone";
    }
}
