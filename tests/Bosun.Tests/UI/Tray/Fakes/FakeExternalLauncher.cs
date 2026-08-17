using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray.Fakes;

internal sealed class FakeExternalLauncher : IExternalLauncher
{
    public List<string> TerminalOpens { get; } = [];
    public List<string> ExplorerOpens { get; } = [];

    public void OpenTerminal(string hostKey) => TerminalOpens.Add(hostKey);

    public void OpenInExplorer(string drive) => ExplorerOpens.Add(drive);
}
