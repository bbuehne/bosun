using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process.Fakes;

/// <summary>Scriptable port owner. No real TCP table is read.</summary>
internal sealed class FakePortOwnerResolver : IPortOwnerResolver
{
    private readonly Queue<Func<int?>> _script = new();

    /// <summary>Returned once the script is exhausted.</summary>
    public int? Default { get; set; }

    public int CallCount { get; private set; }

    public void Enqueue(int? pid) => _script.Enqueue(() => pid);

    public void EnqueueThrow(Exception ex) => _script.Enqueue(() => throw ex);

    public int? GetListeningProcessId(int port)
    {
        CallCount++;
        return _script.TryDequeue(out var next) ? next() : Default;
    }
}

internal sealed class FakeProcessInspector : IProcessInspector
{
    private readonly Dictionary<int, Func<ProcessDescription?>> _byPid = [];

    public void Set(ProcessDescription? description, int pid) => _byPid[pid] = () => description;

    public void SetThrows(int pid, Exception ex) => _byPid[pid] = () => throw ex;

    public ProcessDescription? Describe(int processId) =>
        _byPid.TryGetValue(processId, out var f) ? f() : null;
}

/// <summary>Records kills; never touches a real process. By default a killed process is reported
/// as exited on the next check.</summary>
internal sealed class FakeProcessTerminator : IProcessTerminator
{
    public List<(int Pid, DateTimeOffset Start)> KillCalls { get; } = [];

    public bool KillSucceeds { get; set; } = true;

    /// <summary>When false the "process" never exits after being killed.</summary>
    public bool ExitsAfterKill { get; set; } = true;

    public int HasExitedCalls { get; private set; }

    public Action<int>? OnKill { get; set; }

    public bool TryKill(int processId, DateTimeOffset expectedStartTime)
    {
        KillCalls.Add((processId, expectedStartTime));
        if (KillSucceeds)
        {
            OnKill?.Invoke(processId);
        }

        return KillSucceeds;
    }

    public bool HasExited(int processId, DateTimeOffset expectedStartTime)
    {
        HasExitedCalls++;
        return ExitsAfterKill && KillCalls.Count > 0;
    }
}

internal sealed class FakeRcPortGuard : IRcPortGuard
{
    private readonly Queue<RcPortCheck> _script = new();

    public int EnsureFreeCalls { get; private set; }

    public string? Holder { get; set; }

    public Action? OnEnsureFree { get; set; }

    public void Enqueue(RcPortCheck check) => _script.Enqueue(check);

    public Task<RcPortCheck> EnsureFreeAsync(CancellationToken cancellationToken)
    {
        EnsureFreeCalls++;
        OnEnsureFree?.Invoke();
        return Task.FromResult(_script.TryDequeue(out var next) ? next : new RcPortCheck(RcPortCheckOutcome.Free));
    }

    public string? DescribeHolder() => Holder;
}
