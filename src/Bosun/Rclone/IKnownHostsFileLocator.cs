namespace Bosun.Rclone;

/// <summary>
/// Where the <c>known_hosts</c> file rclone verifies SFTP host keys against lives (ADR-025
/// Decision 1). A seam so no test ever resolves, let alone reads, the real user profile.
/// </summary>
public interface IKnownHostsFileLocator
{
    /// <summary>The absolute, fully expanded path of the file. Never read or written by Bosun
    /// (ADR-025 Decision 4); it is only handed to rclone.</summary>
    string Path { get; }
}

/// <summary>
/// The file <c>ssh.exe</c> uses: <c>%USERPROFILE%\.ssh\known_hosts</c>, resolved the way
/// <see cref="Configuration.ConfigValidator.ExpandHome"/> resolves <c>~</c> (the user-profile
/// special folder), so the two agree on what "home" is.
/// </summary>
public sealed class UserProfileKnownHostsFileLocator(Func<string>? userProfile = null) : IKnownHostsFileLocator
{
    private readonly Func<string> _userProfile =
        userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string Path => System.IO.Path.Combine(_userProfile(), ".ssh", "known_hosts");
}
