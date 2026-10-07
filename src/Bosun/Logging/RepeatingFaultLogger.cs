namespace Bosun.Logging;

/// <summary>What a caller should do with one more sighting of a fault.</summary>
public enum FaultLogAction
{
    /// <summary>The same fault as last time, and a reminder is not yet due: write nothing.</summary>
    Suppress,

    /// <summary>First sighting, or the fault's kind changed: write the full message.</summary>
    Log,

    /// <summary>Same fault, reminder interval elapsed: write one short "still failing" line.</summary>
    Remind,
}

/// <param name="Action">What to do.</param>
/// <param name="Count">Sightings of the current kind so far, this one included.</param>
/// <param name="Since">Local time of the first sighting of the current kind.</param>
public readonly record struct FaultObservation(FaultLogAction Action, int Count, DateTimeOffset Since);

/// <summary>
/// Keeps a fault that repeats on a timer from filling the log (bs-qcs): the first sighting is
/// logged in full, repeats of the same <c>kind</c> are silent, one reminder per interval carries
/// the running count, and a change of kind is logged at once. Faults are tracked per
/// <c>source</c>, so two hosts or two call sites do not disturb each other. It decides; the caller
/// writes the line, which keeps log levels and message wording with the call site. Time comes from
/// the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class RepeatingFaultLogger(TimeProvider timeProvider, TimeSpan? reminderInterval = null)
{
    public static readonly TimeSpan DefaultReminderInterval = TimeSpan.FromMinutes(10);

    private readonly TimeSpan interval = reminderInterval ?? DefaultReminderInterval;
    private readonly Dictionary<string, Sighting> faults = [];

    /// <summary>Records one sighting of <paramref name="kind"/> from <paramref name="source"/>.</summary>
    public FaultObservation Observe(string source, string kind)
    {
        lock (faults)
        {
            var now = timeProvider.GetUtcNow();
            if (!faults.TryGetValue(source, out var sighting) || sighting.Kind != kind)
            {
                sighting = new Sighting(kind, timeProvider.GetLocalNow(), now);
                faults[source] = sighting;
                return new FaultObservation(FaultLogAction.Log, 1, sighting.Since);
            }

            sighting.Count++;
            if (now - sighting.LastLoggedUtc < interval)
            {
                return new FaultObservation(FaultLogAction.Suppress, sighting.Count, sighting.Since);
            }

            sighting.LastLoggedUtc = now;
            return new FaultObservation(FaultLogAction.Remind, sighting.Count, sighting.Since);
        }
    }

    /// <summary>
    /// Ends the fault for <paramref name="source"/>. Returns its count and start when there was one,
    /// so the caller can log the recovery exactly once; <see langword="null"/> when nothing was failing.
    /// </summary>
    public FaultObservation? Recover(string source)
    {
        lock (faults)
        {
            return faults.Remove(source, out var sighting)
                ? new FaultObservation(FaultLogAction.Log, sighting.Count, sighting.Since)
                : null;
        }
    }

    private sealed class Sighting(string kind, DateTimeOffset since, DateTimeOffset loggedUtc)
    {
        public string Kind { get; } = kind;
        public DateTimeOffset Since { get; } = since;
        public int Count { get; set; } = 1;
        public DateTimeOffset LastLoggedUtc { get; set; } = loggedUtc;
    }
}
