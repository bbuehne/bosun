namespace Bosun.Tests.Supervisor.Independent.RepairAll;

/// <summary>One outbound call (probe or rc) or one real effect on the fake mount table.</summary>
/// <param name="Sequence">Global order across every event, probes and rc calls alike.</param>
/// <param name="Kind">See the constants on <see cref="RepairEventLog"/>.</param>
/// <param name="Subject">A drive (rc events) or a host key (probe events).</param>
/// <param name="Ok">False when the call threw or the probe reported a failure.</param>
/// <param name="Listed">For a successful listmounts, what it returned.</param>
/// <param name="TransitionsBefore">How many transitions the supervisor had recorded when this
/// event happened. Lets a test place calls and transitions in one order.</param>
internal sealed record RepairEvent(
    long Sequence,
    string Kind,
    string? Subject,
    bool Ok,
    IReadOnlyList<string>? Listed,
    int TransitionsBefore);

/// <summary>
/// One ordered log across BOTH collaborators (<see cref="RecordingProbe"/> and
/// <see cref="RepairRcloneDouble"/>). The bs-aoz claim "remount only after a fresh probe that
/// happened after the drain" is an ordering claim across the two, so separate per-collaborator
/// lists cannot express it.
/// </summary>
internal sealed class RepairEventLog
{
    public const string Shallow = "probe/shallow";
    public const string Deep = "probe/deep";
    public const string Mount = "mount/mount";
    public const string Unmount = "mount/unmount";
    public const string ListMounts = "mount/listmounts";
    public const string EffectMounted = "effect/mounted";
    public const string EffectUnmounted = "effect/unmounted";

    private readonly object gate = new();
    private readonly List<RepairEvent> events = [];
    private long sequence;

    /// <summary>Wired by the harness to the supervisor's transition history count.</summary>
    public Func<int> TransitionCount { get; set; } = () => 0;

    public IReadOnlyList<RepairEvent> Events
    {
        get
        {
            lock (gate)
            {
                return events.ToList();
            }
        }
    }

    public long LastSequence
    {
        get
        {
            lock (gate)
            {
                return sequence;
            }
        }
    }

    public void Record(string kind, string? subject, bool ok, IReadOnlyList<string>? listed = null)
    {
        var transitions = TransitionCount();
        lock (gate)
        {
            events.Add(new RepairEvent(++sequence, kind, subject, ok, listed, transitions));
        }
    }

    /// <summary>Events with a sequence number strictly greater than <paramref name="after"/>.</summary>
    public IReadOnlyList<RepairEvent> Since(long after) => Events.Where(e => e.Sequence > after).ToList();

    public string Dump(long after = 0) =>
        string.Join(Environment.NewLine, Since(after).Select(e =>
            $"  #{e.Sequence} {e.Kind} {e.Subject}{(e.Ok ? string.Empty : " FAILED")}" +
            $"{(e.Listed is null ? string.Empty : $" -> [{string.Join(',', e.Listed)}]")} (after {e.TransitionsBefore} transitions)"));
}
