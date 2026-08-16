namespace Bosun.Configuration;

/// <summary>
/// Thrown by <see cref="HostConfigStore.Load"/> when the very first load of <c>hosts.toml</c> is
/// invalid. Unlike a failed reload — surfaced via <see cref="IHostConfigStore.ConfigReloadFailed"/>
/// while the store keeps serving the last valid config — there is no previous config to fall
/// back to on startup, so the caller must fail fast.
/// </summary>
public sealed class InvalidConfigException : Exception
{
    public string SourceName { get; }
    public IReadOnlyList<string> Errors { get; }

    public InvalidConfigException(string sourceName, IReadOnlyList<string> errors)
        : base(BuildMessage(sourceName, errors))
    {
        SourceName = sourceName;
        Errors = errors;
    }

    private static string BuildMessage(string sourceName, IReadOnlyList<string> errors) =>
        $"{sourceName}: invalid configuration ({errors.Count} error(s)):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
}
