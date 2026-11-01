using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray.Fakes;

internal sealed class FakeExternalLauncher : IExternalLauncher
{
    public List<string> TerminalOpens { get; } = [];
    public List<string> ExplorerOpens { get; } = [];

    public List<IReadOnlyList<string>> ClaudeRestores { get; } = [];

    public void RestoreClaudeSessions(IReadOnlyList<string> wtArguments) => ClaudeRestores.Add(wtArguments);

    public void OpenTerminal(string hostKey) => TerminalOpens.Add(hostKey);

    public void OpenInExplorer(string drive) => ExplorerOpens.Add(drive);
}
