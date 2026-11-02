using System.IO;

namespace Bosun.Rclone.Process;

/// <summary>
/// The only things <see cref="RcloneExecutableResolver"/> asks the machine about: environment
/// variables, whether a file exists, and the sub-directories of a directory. A seam so the
/// resolver's tests use a fake and never touch the real PATH or filesystem (CLAUDE.md worktree
/// rules); the real implementation is <see cref="SystemExecutableSearchEnvironment"/>.
/// </summary>
public interface IExecutableSearchEnvironment
{
    /// <summary>The value of an environment variable of this process, or <see langword="null"/>
    /// when it is not set. Names are case-insensitive, as on Windows.</summary>
    string? GetEnvironmentVariable(string name);

    bool FileExists(string path);

    /// <summary>Full paths of the immediate sub-directories of <paramref name="parent"/> whose
    /// names match <paramref name="searchPattern"/> (<c>*</c> and <c>?</c> wildcards). Empty when
    /// <paramref name="parent"/> does not exist or cannot be read.</summary>
    IReadOnlyList<string> GetSubdirectories(string parent, string searchPattern);
}

/// <summary>Production <see cref="IExecutableSearchEnvironment"/>. Never exercised by the default
/// test suite.</summary>
public sealed class SystemExecutableSearchEnvironment : IExecutableSearchEnvironment
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> GetSubdirectories(string parent, string searchPattern)
    {
        try
        {
            return Directory.Exists(parent)
                ? [.. Directory.EnumerateDirectories(parent, searchPattern)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>
/// What <see cref="IRcloneExecutableResolver.Resolve"/> found. <see cref="Searched"/> is every
/// place that was looked at, in order, in words a person can read; it is what the "rclone was not
/// found" message prints.
/// </summary>
public sealed record RcloneExecutableResolution
{
    /// <summary>Full path to use, or <see langword="null"/> when rclone was not found.</summary>
    public string? Path { get; init; }

    /// <summary>Where <see cref="Path"/> came from (e.g. <c>PATH</c>, <c>winget Links</c>).
    /// <see langword="null"/> when nothing was found.</summary>
    public string? Source { get; init; }

    public required IReadOnlyList<string> Searched { get; init; }

    public bool Found => Path is not null;
}

/// <summary>Turns the configured rclone name into something launchable (bs-64k).</summary>
public interface IRcloneExecutableResolver
{
    RcloneExecutableResolution Resolve(string configuredPath);
}

/// <summary>
/// Finds <c>rclone.exe</c>: PATH first, then the places the common installers put it (bs-64k).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just rely on PATH.</b> A process inherits PATH when it starts. If rclone is
/// installed, and added to PATH, after Bosun (or the Explorer session that launched it) began,
/// Bosun never sees the change, even though <c>rclone.exe</c> is sitting in a standard place.
/// Windows works this way for every program; the fallbacks below make Bosun cope with it.
/// </para>
/// <para>
/// <b>Order.</b> PATH wins when it has rclone (so anyone who deliberately put a particular
/// rclone first on PATH gets that one). Then, in this order: <c>%LOCALAPPDATA%\rclone</c>,
/// <c>%ProgramFiles%\rclone</c>, winget (user <c>Links</c> shim, user package folder, machine
/// package folder), scoop, Chocolatey. Each is a plain file check; nothing is run.
/// </para>
/// <para>
/// <b>Not resolved.</b> A configured value that already contains a directory (an absolute path,
/// or <c>.\rclone</c>) is the user's explicit choice and is passed through untouched.
/// </para>
/// </remarks>
public sealed class RcloneExecutableResolver(IExecutableSearchEnvironment environment) : IRcloneExecutableResolver
{
    /// <summary>winget names a portable package's folder <c>&lt;PackageId&gt;_&lt;Source&gt;_&lt;hash&gt;</c>,
    /// and Rclone.Rclone's zip puts <c>rclone.exe</c> one level further down, in
    /// <c>rclone-v&lt;version&gt;-windows-&lt;arch&gt;</c> (the manifest's NestedInstallerFiles
    /// RelativeFilePath). Both levels carry a version or hash, hence the wildcards.</summary>
    internal const string WingetPackageFolderPattern = "Rclone.Rclone_*";

    internal const string WingetExtractFolderPattern = "rclone-*";

    public RcloneExecutableResolution Resolve(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        if (Path.IsPathRooted(configuredPath) || configuredPath.AsSpan().IndexOfAny('\\', '/') >= 0)
        {
            return new RcloneExecutableResolution
            {
                Path = configuredPath,
                Source = "configured path",
                Searched = [configuredPath],
            };
        }

        var fileName = configuredPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configuredPath
            : configuredPath + ".exe";

        var searched = new List<string> { "PATH" };

        foreach (var directory in PathDirectories())
        {
            var candidate = Path.Combine(directory, fileName);
            if (environment.FileExists(candidate))
            {
                return Found(candidate, "PATH", searched);
            }
        }

        foreach (var location in FallbackLocations(fileName))
        {
            searched.Add(location.Description);
            foreach (var candidate in location.Candidates())
            {
                if (environment.FileExists(candidate))
                {
                    return Found(candidate, location.Source, searched);
                }
            }
        }

        return new RcloneExecutableResolution { Searched = searched };
    }

    private static RcloneExecutableResolution Found(string path, string source, List<string> searched) =>
        new() { Path = path, Source = source, Searched = searched };

    private IEnumerable<string> PathDirectories()
    {
        var raw = environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(raw))
        {
            yield break;
        }

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length > 0)
            {
                yield return directory;
            }
        }
    }

    private sealed record Location(string Source, string Description, Func<IEnumerable<string>> Candidates);

    private IEnumerable<Location> FallbackLocations(string fileName)
    {
        var localAppData = Variable("LOCALAPPDATA");
        var programFiles = Variable("ProgramFiles");
        var userProfile = Variable("USERPROFILE");
        var programData = Variable("ProgramData");

        if (localAppData is not null)
        {
            yield return Single("%LOCALAPPDATA%\\rclone", Path.Combine(localAppData, "rclone", fileName));
        }

        if (programFiles is not null)
        {
            yield return Single("%ProgramFiles%\\rclone", Path.Combine(programFiles, "rclone", fileName));
        }

        if (localAppData is not null)
        {
            yield return Single("winget Links", Path.Combine(localAppData, "Microsoft", "WinGet", "Links", fileName));
            yield return WingetPackages(
                "winget packages (user)", Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"), fileName);
        }

        if (programFiles is not null)
        {
            yield return WingetPackages(
                "winget packages (machine)", Path.Combine(programFiles, "WinGet", "Packages"), fileName);
        }

        // Scoop honours %SCOOP% as its root; otherwise the root is %USERPROFILE%\scoop.
        var scoopRoot = Variable("SCOOP") ?? (userProfile is null ? null : Path.Combine(userProfile, "scoop"));
        if (scoopRoot is not null)
        {
            yield return Single("scoop", Path.Combine(scoopRoot, "shims", fileName));
        }

        // Chocolatey honours %ChocolateyInstall% as its root; otherwise %ProgramData%\chocolatey.
        var chocolateyRoot = Variable("ChocolateyInstall")
            ?? (programData is null ? null : Path.Combine(programData, "chocolatey"));
        if (chocolateyRoot is not null)
        {
            yield return Single("Chocolatey", Path.Combine(chocolateyRoot, "bin", fileName));
        }
    }

    private static Location Single(string source, string path) => new(source, path, () => [path]);

    private Location WingetPackages(string source, string packagesRoot, string fileName) => new(
        source,
        Path.Combine(packagesRoot, WingetPackageFolderPattern, WingetExtractFolderPattern, fileName),
        () => WingetCandidates(packagesRoot, fileName));

    /// <summary>Expands the version/hash wildcards. Newest-looking name first (descending), so a
    /// stray older extract folder never beats the current one. The caller applies its own
    /// existence check to each candidate.</summary>
    private IEnumerable<string> WingetCandidates(string packagesRoot, string fileName)
    {
        foreach (var package in Descending(environment.GetSubdirectories(packagesRoot, WingetPackageFolderPattern)))
        {
            foreach (var extract in Descending(environment.GetSubdirectories(package, WingetExtractFolderPattern)))
            {
                yield return Path.Combine(extract, fileName);
            }
        }
    }

    private static IEnumerable<string> Descending(IReadOnlyList<string> paths) =>
        paths.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase);

    private string? Variable(string name)
    {
        var value = environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim('"');
    }
}
