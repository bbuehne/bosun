using Bosun.Terminal;

namespace Bosun.Tests.Terminal.Fakes;

/// <summary>
/// Records the argument list instead of starting <c>wt.exe</c> (bs-e28). No test in the default
/// suite may launch a real Terminal.
/// </summary>
internal sealed class FakeTerminalTabLauncher : ITerminalTabLauncher
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>When set, <see cref="OpenTabs"/> throws it after recording the call.</summary>
    public Exception? ThrowOnOpen { get; set; }

    public void OpenTabs(IReadOnlyList<string> wtArguments)
    {
        Calls.Add(wtArguments.ToList());
        if (ThrowOnOpen is not null)
        {
            throw ThrowOnOpen;
        }
    }
}
