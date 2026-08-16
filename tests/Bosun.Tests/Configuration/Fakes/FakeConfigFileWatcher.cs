using Bosun.Configuration;

namespace Bosun.Tests.Configuration.Fakes;

/// <summary>A manually-triggered <see cref="IConfigFileWatcher"/> -- the test calls
/// <see cref="RaiseChanged"/> in place of a real filesystem event, so behaviour never depends on
/// real <see cref="FileSystemWatcher"/> timing (CLAUDE.md worktree-safety rules).</summary>
internal sealed class FakeConfigFileWatcher : IConfigFileWatcher
{
    public event Action? Changed;

    public int DisposeCallCount { get; private set; }

    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose() => DisposeCallCount++;
}
