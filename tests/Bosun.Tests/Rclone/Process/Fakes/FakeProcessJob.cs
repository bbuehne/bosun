using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process.Fakes;

/// <summary>Records every <see cref="IProcessJob.TryAssign"/> call. No real Job Object.</summary>
internal sealed class FakeProcessJob : IProcessJob
{
    public int AssignCalls { get; private set; }

    public bool Succeeds { get; set; } = true;

    public Exception? Throws { get; set; }

    public bool TryAssign(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out string error)
    {
        AssignCalls++;
        if (Throws is not null)
        {
            throw Throws;
        }

        error = Succeeds ? string.Empty : "access is denied (fake)";
        return Succeeds;
    }
}
