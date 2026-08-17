using Bosun.Hosting;

namespace Bosun.Tests.Hosting.Fakes;

/// <summary>
/// Records calls instead of opening a real named <see cref="System.Threading.EventWaitHandle"/>.
/// Lets <see cref="SingleInstanceOrchestratorTests"/> raise <see cref="ActivationRequested"/>
/// directly via <see cref="RaiseActivationRequested"/>, simulating what a real secondary launch
/// calling <see cref="RequestActivation"/> would eventually cause on the listening side, without
/// needing a second real process or thread to produce it.
/// </summary>
public sealed class FakeInstanceActivationChannel : IInstanceActivationChannel
{
    public int StartListeningCallCount { get; private set; }

    public int RequestActivationCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public event EventHandler? ActivationRequested;

    public void StartListening() => StartListeningCallCount++;

    public void RequestActivation() => RequestActivationCallCount++;

    public void RaiseActivationRequested() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => DisposeCallCount++;
}
