using Microsoft.Win32;

namespace Bosun.Rclone;

/// <summary>
/// Real <see cref="IWinFspDetector"/> (bs-5mt). Reads the <c>InstallDir</c> value under
/// WinFsp's installation registry key. Source:
/// https://github.com/winfsp/winfsp/blob/master/doc/WinFsp-Registry-Settings.md -- "Contains the
/// WinFsp installation directory. ... On a 64-bit system (x64 or ARM64) this key is stored in
/// the 32-bit portion of the registry and its true location is
/// <c>HKLM\SOFTWARE\WOW6432Node\WinFsp</c>." <see cref="RegistryView.Registry32"/> is used
/// explicitly for exactly that reason: it makes the lookup correct regardless of whether Bosun
/// itself is running as a 32-bit or 64-bit process (an unqualified
/// <c>HKLM\SOFTWARE\WinFsp</c> read from a 64-bit process would NOT be transparently redirected
/// to WOW6432Node the way it would from a 32-bit process, and would find nothing).
/// </summary>
/// <remarks>
/// <paramref name="installDirReader"/> mirrors <c>ConfigValidator</c>'s injected
/// <c>identityFileExists</c> pattern: production omits it (real registry read), tests always
/// supply a fake so no test result depends on whether WinFsp happens to be installed on the
/// machine running the tests -- which, per the E3 brief, it is NOT on the maintainer's machine,
/// so a test that accidentally read the real registry would currently pass for the wrong reason
/// and silently start failing the day he installs it.
/// </remarks>
public sealed class WinFspDetector(Func<string?>? installDirReader = null) : IWinFspDetector
{
    private const string RegistryPath = @"SOFTWARE\WinFsp";
    private const string InstallDirValueName = "InstallDir";

    private readonly Func<string?> _installDirReader = installDirReader ?? ReadInstallDirFromRegistry;

    public WinFspDetectionResult Detect()
    {
        var installDir = _installDirReader();

        if (!string.IsNullOrEmpty(installDir))
        {
            return new WinFspDetectionResult
            {
                IsInstalled = true,
                InstallDirectory = installDir,
                Message = $"WinFsp found at '{installDir}'.",
            };
        }

        return new WinFspDetectionResult
        {
            IsInstalled = false,
            InstallDirectory = null,
            Message =
                "WinFsp is not installed. Drive mounting is disabled until it is -- terminal " +
                "profiles and SSH sessions are unaffected. Install WinFsp from " +
                "https://winfsp.dev/rel/ and restart Bosun.",
        };
    }

    private static string? ReadInstallDirFromRegistry()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var winFspKey = hklm.OpenSubKey(RegistryPath);
        return winFspKey?.GetValue(InstallDirValueName) as string;
    }
}
