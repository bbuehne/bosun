namespace Bosun.Terminal;

/// <summary>
/// The value Bosun passes to <c>wt.exe</c>'s <c>-p</c> to open a host's generated profile (bs-4ic).
/// Pure; no process, no filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the GUID and not a name.</b> The fragment's profile <c>name</c> is
/// <see cref="Bosun.Configuration.HostConfig.DisplayName"/>, but the identity Bosun owns is the
/// config key (ADR-013 Decision 2): <see cref="FragmentProfileGenerator.CreateProfile"/> emits an
/// explicit <c>guid</c> derived from the key. <c>-p &lt;key&gt;</c> therefore matched nothing
/// whenever <c>display_name</c> differed from the key, and <c>-p &lt;display name&gt;</c> would
/// break the moment the user renames a host. The GUID is stable across renames and is derived by
/// the same call that writes the fragment, so the two cannot disagree.
/// </para>
/// <para>
/// <b>Evidence that <c>-p</c> accepts a GUID.</b> Microsoft's command-line page
/// (learn.microsoft.com/windows/terminal/command-line-arguments) documents <c>-p</c> only in terms
/// of profile names. The actions page
/// (learn.microsoft.com/windows/terminal/customize-settings/actions) says the <c>newTab</c>
/// action's <c>profile</c> takes a "name or GUID", and <c>wt new-tab</c> parses its arguments into
/// that same action. The maintainer then verified it on the real machine on 2026-11-02:
/// <c>wt -w 0 new-tab -p "{guid}"</c>, with the braced GUID of a Bosun profile from the live
/// fragment, opened that profile's commandline. No default-suite test launches <c>wt.exe</c>, so
/// that check is manual and is not re-run by CI.
/// </para>
/// <para>
/// <b>Format.</b> Braced, lowercase hex ("B" format) -- the same string
/// <see cref="BracedGuidJsonConverter"/> writes into the fragment's <c>guid</c> field.
/// </para>
/// </remarks>
public static class TerminalProfileArgument
{
    /// <summary>The braced profile GUID for the host with config key <paramref name="hostKey"/>,
    /// e.g. <c>{65762cab-0682-50e7-b722-2a7bbfadc2a1}</c>.</summary>
    public static string For(string hostKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKey);
        return TerminalGuid.DeriveProfileGuid(FragmentProfileGenerator.AppName, hostKey).ToString("B");
    }
}
