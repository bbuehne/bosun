using System.Diagnostics;

namespace Bosun.Remote.Tests;

/// <summary>
/// A unique temp directory laid out as <c>root\area</c> (the development area), <c>root\outside</c>
/// (a folder that must never be reachable) and <c>root\areaX</c> (a sibling sharing the area's name as
/// a prefix). Everything lives under the system temp folder; nothing touches Bosun's real state.
/// </summary>
internal sealed class TempArea : IDisposable
{
    public TempArea()
    {
        Root = Path.Combine(Path.GetTempPath(), "bosun-remote-tests-" + Guid.NewGuid().ToString("N"));
        Area = Path.Combine(Root, "Dev");
        Outside = Path.Combine(Root, "outside");
        Sibling = Path.Combine(Root, "Development");
        Directory.CreateDirectory(Area);
        Directory.CreateDirectory(Outside);
        Directory.CreateDirectory(Sibling);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "secret");
    }

    public string Root { get; }
    public string Area { get; }
    public string Outside { get; }
    public string Sibling { get; }

    public DevelopmentAreaGuard Guard() => new(Area);

    public string MakeDir(params string[] relativeToArea)
    {
        var path = Path.Combine([Area, .. relativeToArea]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a directory junction (no elevation needed) via <c>mklink /J</c>.</summary>
    public static void CreateJunction(string linkPath, string targetDirectory)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(linkPath);
        psi.ArgumentList.Add(targetDirectory);

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {output}");
        }
    }

    public void Dispose()
    {
        try
        {
            RemoveLinks(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder must not fail a test run.
        }
    }

    // Delete links first (non-recursively) so a recursive delete can never follow one into outside.
    private static void RemoveLinks(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.Delete(entry.FullName, recursive: false);
                }
                else
                {
                    File.Delete(entry.FullName);
                }
            }
            else if (entry is DirectoryInfo)
            {
                RemoveLinks(entry.FullName);
            }
        }
    }
}

/// <summary>
/// A fact that is skipped, with a stated reason, when this machine cannot create symbolic links
/// (needs Developer Mode or elevation). xUnit v2 has no runtime skip, so the probe runs at discovery.
/// </summary>
internal sealed class SymlinkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public SymlinkFactAttribute()
    {
        if (UnavailableReason.Value is { } reason)
        {
            Skip = "Cannot create symbolic links on this machine: " + reason +
                   " (enable Windows Developer Mode to run the symlink tests).";
        }
    }

    private static string? Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bosun-remote-symlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateSymbolicLink(Path.Combine(dir, "link"), dir);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                var link = Path.Combine(dir, "link");
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
