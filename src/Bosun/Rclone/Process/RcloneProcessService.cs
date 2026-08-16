using Bosun.Rclone;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Rclone.Process;

/// <summary>
/// Owns the one long-lived <c>rclone rcd</c> child process (bs-5mt; docs/ARCHITECTURE.md §2,
/// §6). Starts it at launch, health-checks via <c>core/version</c>, restarts it if it dies, and
/// stops it cleanly at exit -- never orphaning it.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Restart on death" without "reconcile mounts from scratch" is worse than not restarting</b>
/// (E3 brief): a freshly restarted <c>rclone rcd</c> knows nothing about any mount Bosun
/// believes is up (<c>mount/listmounts</c> on a new process returns empty), so if nothing
/// reconciles against that fact, in-memory state and reality diverge silently -- exactly the
/// kind of drift Invariant I2 exists to prevent. This class does not itself own mount state
/// (that is E5's <c>IMountSupervisor</c>, not yet built), so it cannot perform the reconciliation
/// itself. What it DOES do is make "a fresh process just became healthy" an unmissable, typed
/// signal: every transition to <see cref="RcloneProcessStatus.Healthy"/> -- the very first start
/// AND every restart -- raises <see cref="StatusChanged"/>. E5 MUST treat every such event as
/// "reconcile every host from scratch". See the remarks on <see cref="RcloneProcessStatus.Healthy"/>.
/// </para>
/// <para>
/// <b>Retry policy.</b> <see cref="RcloneProcessFaultKind.ExecutableNotFound"/> does not retry:
/// rclone missing from PATH will not fix itself, and a tight retry loop against a guaranteed
/// failure would just spam the log without helping anyone -- the actionable message
/// (<see cref="RcloneExecutableNotFoundException"/>) is the whole point. Every other fault kind
/// (<see cref="RcloneProcessFaultKind.LaunchFailed"/>, <see cref="RcloneProcessFaultKind.HealthCheckFailed"/>,
/// <see cref="RcloneProcessFaultKind.ProcessExitedUnexpectedly"/>) retries indefinitely with a
/// fixed delay (<see cref="RcloneProcessServiceOptions.RestartDelay"/>) until
/// <see cref="StopAsync"/> is called -- these are plausibly transient (antivirus killed it, a
/// momentary port conflict, a slow machine on resume from sleep).
/// </para>
/// </remarks>
public sealed class RcloneProcessService(
    IRcloneProcessLauncher launcher,
    IRcloneClient client,
    RcloneProcessServiceOptions options,
    TimeProvider timeProvider,
    ILogger<RcloneProcessService> logger,
    RcloneRcCredential credential) : IHostedService, IAsyncDisposable
{
    private readonly object _gate = new();
    private IRcloneProcessHandle? _handle;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _superviseLoopTask;

    public RcloneProcessStatus Status { get; private set; } = RcloneProcessStatus.Stopped;
    public RcloneProcessFaultKind FaultKind { get; private set; } = RcloneProcessFaultKind.None;
    public string? LastFaultMessage { get; private set; }

    /// <summary>Raised on every status transition, including every <see cref="RcloneProcessStatus.Healthy"/>
    /// transition -- see the class remarks on why that specific transition matters to E5.</summary>
    public event EventHandler<RcloneProcessStatusChangedEventArgs>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts = new CancellationTokenSource();

        var firstAttemptSucceeded = await AttemptStartAsync(cancellationToken).ConfigureAwait(false);

        // ExecutableNotFound is terminal (see class remarks) -- no supervise loop in that case.
        if (!firstAttemptSucceeded && FaultKind == RcloneProcessFaultKind.ExecutableNotFound)
        {
            return;
        }

        // Everything else -- success, or a retriable fault -- hands off to the background loop,
        // which either waits for the healthy process to exit, or keeps retrying. StartAsync
        // itself must return promptly rather than block host startup on indefinite retries.
        _superviseLoopTask = SuperviseAsync(_lifetimeCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts?.Cancel();

        // Deliberately a plain await, not Task.WaitAsync(cancellationToken): empirically,
        // Task.WaitAsync's continuation is not guaranteed to run inline on the thread that
        // completes the antecedent (observed as a genuine, load-dependent race against
        // FakeTimeProvider.Advance() in RcloneProcessServiceTests -- passed in isolation, failed
        // intermittently under full-suite parallel execution), which breaks the deterministic,
        // synchronous-continuation testing style this class and HostProbe both rely on. The loop
        // is expected to unwind quickly once _lifetimeCts is cancelled (WaitForUnexpectedExitAsync
        // reacts to the same token); KillAndConfirmExitAsync below still has its own bounded
        // TimeProvider-driven timeout for the one wait that is genuinely open-ended (the child
        // process confirming exit), so an unbounded wait here does not risk StopAsync hanging.
        if (_superviseLoopTask is not null)
        {
            await _superviseLoopTask.ConfigureAwait(false);
        }

        await KillAndConfirmExitAsync(cancellationToken).ConfigureAwait(false);
        SetStatus(RcloneProcessStatus.Stopped, RcloneProcessFaultKind.None, null);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();

        IRcloneProcessHandle? handle;
        lock (_gate)
        {
            handle = _handle;
            _handle = null;
        }

        handle?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>The ongoing supervise loop: while a process is healthy, wait for it to exit
    /// unexpectedly; whenever there is no healthy process, keep retrying (unless the last
    /// attempt was terminal). Runs until <paramref name="cancellationToken"/> fires (from
    /// <see cref="StopAsync"/>).</summary>
    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Status == RcloneProcessStatus.Healthy)
                {
                    var handle = _handle;
                    if (handle is null)
                    {
                        // Should not happen (Healthy implies _handle is set), but fail safe
                        // rather than spin.
                        await Task.Delay(options.RestartDelay, timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await WaitForUnexpectedExitAsync(handle, cancellationToken).ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    logger.LogWarning(
                        "rclone rcd exited unexpectedly (exit code {ExitCode}); restarting", handle.ExitCode);
                    SetStatus(
                        RcloneProcessStatus.Faulted,
                        RcloneProcessFaultKind.ProcessExitedUnexpectedly,
                        $"rclone rcd exited unexpectedly (exit code {handle.ExitCode}).");
                }

                await Task.Delay(options.RestartDelay, timeProvider, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var succeeded = await AttemptStartAsync(cancellationToken).ConfigureAwait(false);
                if (!succeeded && FaultKind == RcloneProcessFaultKind.ExecutableNotFound)
                {
                    // Became terminal mid-loop (e.g. rclone was uninstalled between restarts).
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>Completes when <paramref name="handle"/> exits for any reason. The caller
    /// distinguishes "we were cancelled" (shutdown) from "it just died" via
    /// <paramref name="cancellationToken"/>.</summary>
    private static async Task WaitForUnexpectedExitAsync(IRcloneProcessHandle handle, CancellationToken cancellationToken)
    {
        if (handle.HasExited)
        {
            return;
        }

        // Deliberately NOT RunContinuationsAsynchronously: this keeps the supervise loop's
        // resumption on the same call stack as whatever raised Exited, matching the synchronous,
        // no-thread-pool-hop style FakeTcpProbeTransport/HostProbe already rely on for
        // deterministic FakeTimeProvider-driven tests (a test can call
        // FakeRcloneProcessHandle.SimulateExit() and know the loop has already reached its next
        // await point by the time that call returns, with no polling or real wait needed).
        var exitedTcs = new TaskCompletionSource();
        void OnExited(object? sender, EventArgs e) => exitedTcs.TrySetResult();

        handle.Exited += OnExited;
        try
        {
            if (handle.HasExited)
            {
                return;
            }

            await using var registration = cancellationToken.Register(() => exitedTcs.TrySetCanceled(cancellationToken));
            await exitedTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation means shutdown, not an unexpected exit -- caller checks the token.
        }
        finally
        {
            handle.Exited -= OnExited;
        }
    }

    /// <summary>One attempt: launch the process, then wait for it to become healthy. Updates
    /// <see cref="Status"/>/<see cref="FaultKind"/> and raises <see cref="StatusChanged"/>
    /// exactly once for the outcome. Returns <see langword="true"/> only on a confirmed-healthy
    /// process.</summary>
    private async Task<bool> AttemptStartAsync(CancellationToken cancellationToken)
    {
        SetStatus(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);

        IRcloneProcessHandle handle;
        try
        {
            handle = launcher.Start(BuildStartInfo());
        }
        catch (RcloneExecutableNotFoundException ex)
        {
            logger.LogError(ex, "rclone executable not found");
            SetStatus(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ExecutableNotFound, ex.Message);
            return false;
        }
        catch (RcloneProcessLaunchException ex)
        {
            logger.LogError(ex, "rclone rcd failed to start");
            SetStatus(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.LaunchFailed, ex.Message);
            return false;
        }

        lock (_gate)
        {
            _handle = handle;
        }

        var healthy = await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
        if (!healthy)
        {
            var message =
                $"rclone rcd started but did not respond to core/version within {options.HealthCheckTimeout}.";
            logger.LogError(message);

            handle.Kill();
            lock (_gate)
            {
                _handle = null;
            }

            SetStatus(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckFailed, message);
            return false;
        }

        logger.LogInformation("rclone rcd is healthy on loopback port {Port}", options.RcloneRcPort);
        SetStatus(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        return true;
    }

    /// <summary>Polls <c>core/version</c> at <see cref="RcloneProcessServiceOptions.HealthCheckPollInterval"/>
    /// until it succeeds or <see cref="RcloneProcessServiceOptions.HealthCheckTimeout"/> elapses.
    /// All delays go through the injected <see cref="TimeProvider"/> -- no real wall-clock wait
    /// (CLAUDE.md worktree-safety rules).</summary>
    private async Task<bool> WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + options.HealthCheckTimeout;

        while (true)
        {
            try
            {
                await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "core/version health check attempt failed, will retry if time remains");
            }

            if (timeProvider.GetUtcNow() + options.HealthCheckPollInterval > deadline)
            {
                return false;
            }

            await Task.Delay(options.HealthCheckPollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task KillAndConfirmExitAsync(CancellationToken cancellationToken)
    {
        IRcloneProcessHandle? handle;
        lock (_gate)
        {
            handle = _handle;
            _handle = null;
        }

        if (handle is null)
        {
            return;
        }

        handle.Kill();

        // Same TimeProvider-driven-timer-cancels-a-linked-token shape as HostProbe's timeout
        // wiring (src/Bosun/Probe/HostProbe.cs) -- proven deterministic under FakeTimeProvider in
        // HostProbeShallowTests/HostProbeDeepTests. Chosen deliberately over the single-constructor
        // form `new CancellationTokenSource(TimeSpan, TimeProvider)`, which was tried first: this
        // path's continuation, once StopAsync's wait for the supervise loop resolves on a
        // ThreadPool thread rather than inline (see the note on StopAsync above), is not
        // guaranteed to have registered its timer before a test's next FakeTimeProvider.Advance()
        // call, and the alternate constructor did not resolve that on its own in testing -- see
        // RcloneProcessServiceTests.StopAsync_does_not_orphan_the_process_even_if_it_never_confirms_exit
        // and its AdvanceUntilCompleteAsync helper, which handles the remaining race robustly.
        using var timeoutSource = new CancellationTokenSource();
        using var timer = timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource, options.StopTimeout, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await WaitForUnexpectedExitAsync(handle, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("rclone rcd did not confirm exit within {Timeout}; abandoning the wait", options.StopTimeout);
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// <c>rclone rcd --rc-addr 127.0.0.1:&lt;port&gt; --config &lt;path&gt;</c>, with
    /// <c>RCLONE_RC_USER</c>/<c>RCLONE_RC_PASS</c> set in the child's environment (bs-ard).
    /// Loopback ONLY, never <c>0.0.0.0</c> or a LAN-reachable address -- but loopback alone is
    /// NOT sufficient, and the remark this replaces was wrong to claim otherwise.
    /// </summary>
    /// <remarks>
    /// <b>Corrected citation (bs-ard).</b> This used to cite https://rclone.org/commands/rclone_rcd/
    /// ("By default this will serve files without needing a login") to claim no additional
    /// <c>--rc-user</c>/<c>--rc-pass</c>/<c>--rc-no-auth</c> flag was required for a loopback
    /// bind. That claim was verified WRONG against a real rclone v1.75.0 binary: every rc
    /// endpoint except <c>core/version</c> returns HTTP 403 ("authentication must be set up on
    /// the rc server ... or the --rc-no-auth flag must be in use") unless rcd is given
    /// credentials or started with <c>--rc-no-auth</c>. Bosun uses neither
    /// <c>--rc-user</c>/<c>--rc-pass</c> command-line flags (a command line is readable by any
    /// same-user process, e.g. via <c>Win32_Process.CommandLine</c> -- the exact API
    /// <c>ISessionMonitor</c> uses to enumerate <c>ssh.exe</c>) NOR <c>--rc-no-auth</c> (which
    /// would remove auth entirely for every local caller, including <c>mount/mount</c> and
    /// <c>mount/unmount</c>). Instead, a cryptographically random per-Bosun-launch credential
    /// (<see cref="RcloneRcCredential"/>) is passed via the <c>RCLONE_RC_USER</c>/
    /// <c>RCLONE_RC_PASS</c> environment variables, which rclone documents rcd reads
    /// (https://rclone.org/rc/#authentication) as equivalent to the command-line flags. See the
    /// class remarks on <see cref="RcloneRcCredential"/> for why the SAME credential is reused
    /// across every restart rather than rotated per attempt.
    /// </remarks>
    private RcloneProcessStartInfo BuildStartInfo() => new()
    {
        ExecutablePath = options.ExecutablePath,
        Arguments =
        [
            "rcd",
            "--rc-addr",
            $"127.0.0.1:{options.RcloneRcPort}",
            "--config",
            options.RcloneConfigPath,
        ],
        EnvironmentVariables = new Dictionary<string, string>
        {
            ["RCLONE_RC_USER"] = credential.UserName,
            ["RCLONE_RC_PASS"] = credential.Password,
        },
    };

    private void SetStatus(RcloneProcessStatus status, RcloneProcessFaultKind faultKind, string? detail)
    {
        Status = status;
        FaultKind = faultKind;
        LastFaultMessage = detail;
        StatusChanged?.Invoke(this, new RcloneProcessStatusChangedEventArgs(status, faultKind, detail));
    }
}
