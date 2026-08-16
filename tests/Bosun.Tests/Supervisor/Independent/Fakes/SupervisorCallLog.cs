namespace Bosun.Tests.Supervisor.Independent.Fakes;

/// <summary>
/// One ordered log of every outbound call the supervisor makes, across BOTH collaborators
/// (<see cref="ProbeDouble"/> and <see cref="RcloneDouble"/> append to the same instance).
/// </summary>
/// <remarks>
/// Per-collaborator call lists cannot express the single most important ordering claim in this
/// project: that a <c>mount/mount</c> is always preceded by a <em>successful deep probe for that
/// same host</em> (Invariant I1, docs/ARCHITECTURE.md §4 rule 2). Two separate lists can each look
/// correct while the calls interleaved in the wrong order. A shared log makes that assertable.
/// </remarks>
internal sealed class SupervisorCallLog
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries => entries;

    public void Record(string entry) => entries.Add(entry);

    /// <summary>The log filtered to entries whose text starts with any of
    /// <paramref name="prefixes"/>, preserving order.</summary>
    public IReadOnlyList<string> Filtered(params string[] prefixes) =>
        entries.Where(e => prefixes.Any(p => e.StartsWith(p, StringComparison.Ordinal))).ToList();

    public int CountWithPrefix(string prefix) =>
        entries.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));

    public string Dump() => string.Join(Environment.NewLine, entries);
}
