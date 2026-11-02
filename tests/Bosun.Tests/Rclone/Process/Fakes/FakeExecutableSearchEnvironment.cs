using System.Text.RegularExpressions;
using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process.Fakes;

/// <summary>
/// In-memory <see cref="IExecutableSearchEnvironment"/>: a dictionary of environment variables
/// and a set of files. Directories exist implicitly as the parents of added files. Nothing here
/// touches the real PATH, environment or filesystem (bs-64k).
/// </summary>
internal sealed class FakeExecutableSearchEnvironment : IExecutableSearchEnvironment
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public const string LocalAppData = @"C:\Users\test\AppData\Local";
    public const string ProgramFiles = @"C:\Program Files";
    public const string UserProfile = @"C:\Users\test";
    public const string ProgramData = @"C:\ProgramData";

    /// <summary>Every path <see cref="FileExists"/> was asked about, in order.</summary>
    public List<string> FileExistsCalls { get; } = [];

    /// <summary>A machine with the usual Windows variables set and an empty disk and PATH.</summary>
    public static FakeExecutableSearchEnvironment Standard()
    {
        var env = new FakeExecutableSearchEnvironment();
        env.SetVariable("LOCALAPPDATA", LocalAppData);
        env.SetVariable("ProgramFiles", ProgramFiles);
        env.SetVariable("USERPROFILE", UserProfile);
        env.SetVariable("ProgramData", ProgramData);
        env.SetVariable("PATH", @"C:\Windows\system32;C:\Windows");
        return env;
    }

    public FakeExecutableSearchEnvironment SetVariable(string name, string? value)
    {
        if (value is null)
        {
            _variables.Remove(name);
        }
        else
        {
            _variables[name] = value;
        }

        return this;
    }

    public FakeExecutableSearchEnvironment AddFile(string path)
    {
        _files.Add(path);
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            _directories.Add(dir);
        }

        return this;
    }

    public FakeExecutableSearchEnvironment RemoveFile(string path)
    {
        _files.Remove(path);
        return this;
    }

    public string? GetEnvironmentVariable(string name) => _variables.GetValueOrDefault(name);

    public bool FileExists(string path)
    {
        FileExistsCalls.Add(path);
        return _files.Contains(path);
    }

    public IReadOnlyList<string> GetSubdirectories(string parent, string searchPattern)
    {
        var wanted = Path.TrimEndingDirectorySeparator(parent);
        var pattern = new Regex(
            "^" + Regex.Escape(searchPattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return
        [
            .. _directories.Where(d =>
                string.Equals(Path.GetDirectoryName(d), wanted, StringComparison.OrdinalIgnoreCase) &&
                pattern.IsMatch(Path.GetFileName(d))),
        ];
    }
}
