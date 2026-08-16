using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process.Fakes;

/// <summary>
/// Scriptable <see cref="IRcloneProcessLauncher"/> -- no default-suite test may start a real
/// process (CLAUDE.md worktree-safety rules). Each call to <see cref="Start"/> dequeues the next
/// scripted outcome; once the queue drains, it returns a fresh healthy
/// <see cref="FakeRcloneProcessHandle"/>.
/// </summary>
internal sealed class FakeRcloneProcessLauncher : IRcloneProcessLauncher
{
    private readonly Queue<Func<IRcloneProcessHandle>> _script = new();

    public List<RcloneProcessStartInfo> StartCalls { get; } = [];

    public void EnqueueSuccess(IRcloneProcessHandle handle) => _script.Enqueue(() => handle);

    public void EnqueueExecutableNotFound() =>
        _script.Enqueue(() => throw new RcloneExecutableNotFoundException("rclone", new InvalidOperationException("not found")));

    public void EnqueueLaunchFailure() =>
        _script.Enqueue(() => throw new RcloneProcessLaunchException("rclone", new InvalidOperationException("boom")));

    public IRcloneProcessHandle Start(RcloneProcessStartInfo startInfo)
    {
        StartCalls.Add(startInfo);
        return _script.TryDequeue(out var next) ? next() : new FakeRcloneProcessHandle();
    }
}
