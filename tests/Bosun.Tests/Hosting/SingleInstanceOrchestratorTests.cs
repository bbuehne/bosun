using Bosun.Hosting;
using Bosun.Tests.Hosting.Fakes;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers bs-2wa's acceptance criterion: "launching Bosun twice leaves exactly one process ...
/// Testable without WPF by putting the guard behind an interface and asserting the second
/// acquisition fails and the host is never built." Every test here uses
/// <see cref="FakeSingleInstanceGuard"/> and <see cref="FakeInstanceActivationChannel"/> -- never
/// <see cref="MutexSingleInstanceGuard"/> or <see cref="EventWaitHandleActivationChannel"/> -- so
/// nothing here touches a real named OS object, matching CLAUDE.md's worktree-safety rules.
/// </summary>
public sealed class SingleInstanceOrchestratorTests
{
    [Fact]
    public void TryBecomePrimary_ReturnsTrue_AndStartsListening_WhenTheGuardIsAcquired()
    {
        var guard = new FakeSingleInstanceGuard(acquireResult: true);
        var channel = new FakeInstanceActivationChannel();
        var orchestrator = new SingleInstanceOrchestrator(guard, channel);

        var isPrimary = orchestrator.TryBecomePrimary();

        Assert.True(isPrimary);
        Assert.Equal(1, guard.TryAcquireCallCount);
        Assert.Equal(1, channel.StartListeningCallCount);
        Assert.Equal(0, channel.RequestActivationCallCount);
    }

    [Fact]
    public void TryBecomePrimary_ReturnsFalse_AndSignalsTheRunningInstance_WhenTheGuardIsAlreadyHeld()
    {
        var guard = new FakeSingleInstanceGuard(acquireResult: false);
        var channel = new FakeInstanceActivationChannel();
        var orchestrator = new SingleInstanceOrchestrator(guard, channel);

        var isPrimary = orchestrator.TryBecomePrimary();

        Assert.False(isPrimary);
        Assert.Equal(1, channel.RequestActivationCallCount);
        Assert.Equal(0, channel.StartListeningCallCount);
    }

    [Fact]
    public void TryBecomePrimary_Failing_NeverInvokesTheCallersHostFactory()
    {
        // The literal acceptance criterion from bs-2wa: a second launch must not have touched
        // anything -- no log directory, no rclone rcd, no fragment write, no mount -- by the time
        // it exits. Modelled here the same way App.xaml.cs uses this class: the host is only ever
        // built if TryBecomePrimary returns true.
        var guard = new FakeSingleInstanceGuard(acquireResult: false);
        var channel = new FakeInstanceActivationChannel();
        var orchestrator = new SingleInstanceOrchestrator(guard, channel);

        var hostFactoryCallCount = 0;
        object HostFactory()
        {
            hostFactoryCallCount++;
            return new object();
        }

        if (orchestrator.TryBecomePrimary())
        {
            HostFactory();
        }

        Assert.Equal(0, hostFactoryCallCount);
    }

    [Fact]
    public void ActivationRequested_ForwardsFromTheUnderlyingChannel()
    {
        var guard = new FakeSingleInstanceGuard(acquireResult: true);
        var channel = new FakeInstanceActivationChannel();
        var orchestrator = new SingleInstanceOrchestrator(guard, channel);

        var raised = 0;
        orchestrator.ActivationRequested += (_, _) => raised++;

        orchestrator.TryBecomePrimary();
        channel.RaiseActivationRequested();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ActivationRequested_CanBeUnsubscribed()
    {
        var guard = new FakeSingleInstanceGuard(acquireResult: true);
        var channel = new FakeInstanceActivationChannel();
        var orchestrator = new SingleInstanceOrchestrator(guard, channel);

        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;

        orchestrator.ActivationRequested += Handler;
        orchestrator.ActivationRequested -= Handler;
        channel.RaiseActivationRequested();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Dispose_DisposesBothTheGuardAndTheActivationChannel()
    {
        var guard = new FakeSingleInstanceGuard(acquireResult: true);
        var channel = new FakeInstanceActivationChannel();
        var orchestrator = new SingleInstanceOrchestrator(guard, channel);

        orchestrator.TryBecomePrimary();
        orchestrator.Dispose();

        Assert.Equal(1, guard.DisposeCallCount);
        Assert.Equal(1, channel.DisposeCallCount);
    }
}
