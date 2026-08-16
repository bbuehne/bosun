using Bosun.Configuration;

namespace Bosun.Tests.Configuration.Fakes;

/// <summary>
/// A scriptable <see cref="IConfigFileReader"/>. Queue results with <see cref="EnqueueResult"/>
/// / <see cref="EnqueueThrow"/> to simulate a specific sequence of reads (e.g. an empty read from
/// a truncate-then-write race, followed by the real content on retry); once the queue drains,
/// reads fall back to <see cref="Content"/>.
/// </summary>
internal sealed class FakeConfigFileReader : IConfigFileReader
{
    private readonly Queue<Func<string>> _scriptedResults = new();

    public string Content { get; set; } = string.Empty;

    public int ReadCount { get; private set; }

    public void EnqueueResult(string content) => _scriptedResults.Enqueue(() => content);

    public void EnqueueThrow(Exception exception) => _scriptedResults.Enqueue(() => throw exception);

    public string ReadAllText(string path)
    {
        ReadCount++;
        return _scriptedResults.TryDequeue(out var next) ? next() : Content;
    }
}
