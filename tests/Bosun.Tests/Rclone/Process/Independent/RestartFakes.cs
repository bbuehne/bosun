using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process.Independent;

/// <summary>
/// One ordered, thread-safe log of everything the restart tests care about: status changes,
/// kills, exits, port-guard checks and launches. The bs-aoz ordering claims ("Starting before
/// the kill", "exit confirmed before the port guard", "port guard before the launch") are
/// claims across three collaborators, so they need one log.
/// </summary>
internal sealed class RestartLog
{
    private readonly object gate = new();
    private readonly List<string> entries = [];

    public void Add(string entry)
    {
        lock (gate)
        {
            entries.Add(entry);
        }
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToList();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public IReadOnlyList<string> Since(int mark) => Entries.Skip(mark).ToList();

    public string Dump(int mark = 0) => string.Join(Environment.NewLine, Since(mark).Select(e => "  " + e));
}

/// <summary>
/// A fake rcd process. By default it exits as soon as it is killed. With
/// <c>exitOnKill: false</c> it stays "running" after <see cref="Kill"/> until the test calls
/// <see cref="Exit"/>, which is how a test proves the service waits for a confirmed exit.
/// </summary>
internal sealed class TrackingHandle(int id, RestartLog log, bool exitOnKill = true) : IRcloneProcessHandle
{
    private readonly object gate = new();
    private bool hasExited;

    public int Id { get; } = id;

    public bool HasExited
    {
        get
        {
            lock (gate)
            {
                return hasExited;
            }
        }
    }

    public int? ExitCode { get; private set; }

    public int KillCount { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler? Exited;

    public void Kill()
    {
        KillCount++;
        log.Add($"kill#{Id}");
        if (exitOnKill)
        {
            Exit(0);
        }
    }

    /// <summary>The process ends (killed, or died on its own). Raises <see cref="Exited"/> once.</summary>
    public void Exit(int code)
    {
        lock (gate)
        {
            if (hasExited)
            {
                return;
            }

            hasExited = true;
            ExitCode = code;
        }

        log.Add($"exited#{Id}");
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Launches <see cref="TrackingHandle"/>s and remembers every one. At each launch it records how
/// many earlier processes were still alive, so "two rcd processes at once" is an assertable
/// number rather than an inference.
/// </summary>
internal sealed class TrackingLauncher(RestartLog log) : IRcloneProcessLauncher
{
    private readonly object gate = new();
    private readonly Queue<Func<int, IRcloneProcessHandle>> script = new();
    private readonly List<TrackingHandle> handles = [];
    private int attempts;

    public int Attempts
    {
        get
        {
            lock (gate)
            {
                return attempts;
            }
        }
    }

    /// <summary>The largest number of other, still-running processes observed at any launch.</summary>
    public int MaxOthersAliveAtLaunch { get; private set; }

    public IReadOnlyList<TrackingHandle> Handles
    {
        get
        {
            lock (gate)
            {
                return handles.ToList();
            }
        }
    }

    public IReadOnlyList<TrackingHandle> Alive => Handles.Where(h => !h.HasExited).ToList();

    public void EnqueueHandle(bool exitOnKill)
    {
        lock (gate)
        {
            script.Enqueue(id => Track(new TrackingHandle(id, log, exitOnKill)));
        }
    }

    public void EnqueueLaunchFailure()
    {
        lock (gate)
        {
            script.Enqueue(_ => throw new RcloneProcessLaunchException("rclone", new InvalidOperationException("simulated launch failure")));
        }
    }

    public void EnqueueExecutableNotFound()
    {
        lock (gate)
        {
            script.Enqueue(_ => throw new RcloneExecutableNotFoundException("rclone", new InvalidOperationException("simulated: not on PATH")));
        }
    }

    public IRcloneProcessHandle Start(RcloneProcessStartInfo startInfo)
    {
        Func<int, IRcloneProcessHandle>? next;
        int id;
        lock (gate)
        {
            id = ++attempts;
            var othersAlive = handles.Count(h => !h.HasExited);
            MaxOthersAliveAtLaunch = Math.Max(MaxOthersAliveAtLaunch, othersAlive);
            script.TryDequeue(out next);
        }

        try
        {
            var handle = next is null ? Track(new TrackingHandle(id, log)) : next(id);
            log.Add($"launch#{id}");
            return handle;
        }
        catch
        {
            log.Add($"launch-failed#{id}");
            throw;
        }
    }

    private TrackingHandle Track(TrackingHandle handle)
    {
        lock (gate)
        {
            handles.Add(handle);
        }

        return handle;
    }
}

/// <summary>
/// An <see cref="IRcPortGuard"/> that logs every check, returns scripted outcomes, and can hold
/// one chosen call open until the test releases it -- which is how the race tests park a restart
/// in the middle of its launch sequence. The hold honours the caller's cancellation token, as a
/// real asynchronous check would.
/// </summary>
internal sealed class GatedPortGuard(RestartLog log) : IRcPortGuard
{
    private readonly object gate = new();
    private readonly Queue<RcPortCheck> script = new();
    private int calls;
    private int holdCall;
    private bool holdHonoursCancellation = true;
    private TaskCompletionSource? release;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls
    {
        get
        {
            lock (gate)
            {
                return calls;
            }
        }
    }

    public void Enqueue(RcPortCheck check)
    {
        lock (gate)
        {
            script.Enqueue(check);
        }
    }

    /// <summary>The <paramref name="callNumber"/>-th check (1-based) waits until
    /// <see cref="Release"/>. With <paramref name="honourCancellation"/> false, the check finishes
    /// normally even if the caller's token is cancelled meanwhile -- cancellation is cooperative,
    /// and a check part-way through synchronous work (reading the TCP table, say) completes.</summary>
    public void HoldCall(int callNumber, bool honourCancellation = true)
    {
        lock (gate)
        {
            holdCall = callNumber;
            holdHonoursCancellation = honourCancellation;
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Release() => release?.TrySetResult();

    public async Task<RcPortCheck> EnsureFreeAsync(CancellationToken cancellationToken)
    {
        int n;
        TaskCompletionSource? hold;
        bool honour;
        RcPortCheck result;
        lock (gate)
        {
            n = ++calls;
            hold = n == holdCall ? release : null;
            honour = holdHonoursCancellation;
            result = script.TryDequeue(out var next) ? next : new RcPortCheck(RcPortCheckOutcome.Free);
        }

        log.Add($"portguard#{n}");
        if (hold is not null)
        {
            Entered.TrySetResult();
            await (honour ? hold.Task.WaitAsync(cancellationToken) : hold.Task).ConfigureAwait(false);
        }

        return result;
    }

    public string? DescribeHolder() => null;
}
