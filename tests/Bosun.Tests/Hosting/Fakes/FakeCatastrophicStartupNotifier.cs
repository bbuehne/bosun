using Bosun.Hosting;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// Records calls instead of displaying a real <see cref="System.Windows.MessageBox"/>. No test in
/// the default suite may pop a real dialog -- a modal would hang CI forever (CLAUDE.md
/// worktree-safety rules) -- so this fake is the only <see cref="ICatastrophicStartupNotifier"/>
/// any test ever constructs.
/// </summary>
public sealed class FakeCatastrophicStartupNotifier : ICatastrophicStartupNotifier
{
    public int CallCount { get; private set; }
    public string? LastReason { get; private set; }
    public Exception? LastException { get; private set; }

    public void NotifyAndAwaitDismissal(string reason, Exception? exception)
    {
        CallCount++;
        LastReason = reason;
        LastException = exception;
    }
}

