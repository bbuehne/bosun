using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process.Fakes;

/// <summary>
/// Scriptable <see cref="IRcloneProcessHandle"/>. <see cref="SimulateExit"/> raises
/// <see cref="Exited"/> synchronously on the calling thread -- see the "Deliberately NOT
/// RunContinuationsAsynchronously" remark on <see cref="RcloneProcessService"/>'s
/// <c>WaitForUnexpectedExitAsync</c> for why that makes tests deterministic without any polling
/// or real wait.
/// </summary>
internal sealed class FakeRcloneProcessHandle : IRcloneProcessHandle
{
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public int KillCallCount { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler? Exited;

    /// <summary>Simulates the process dying on its own (or having been killed) with
    /// <paramref name="exitCode"/>. Idempotent -- a second call is a no-op, matching real
    /// <see cref="System.Diagnostics.Process"/> semantics (the OS only reports exit once).</summary>
    public void SimulateExit(int exitCode)
    {
        if (HasExited)
        {
            return;
        }

        HasExited = true;
        ExitCode = exitCode;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Kill()
    {
        KillCallCount++;
        SimulateExit(0);
    }

    public void Dispose() => Disposed = true;
}
