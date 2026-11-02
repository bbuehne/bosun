using System.Reflection;
using Bosun.SystemEventIntegration;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SystemEventIntegration.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.SystemEventIntegration.Independent;

/// <summary>
/// bs-u4a item 2, written from docs/ARCHITECTURE.md §3's debounce table rather than from the
/// implementation:
/// </summary>
/// <list type="bullet">
/// <item>Quiet period, 750 ms: the event fires once no raw event has arrived for this long; each raw
/// event restarts the window.</item>
/// <item>Maximum deferral, 5 s: the event fires at the latest 5 s after the first raw event of a
/// burst.</item>
/// <item>"A burst begins with the first raw event after the previous public event; firing ends it,
/// so the next raw event starts a new burst with a fresh 5 s."</item>
/// </list>
/// <remarks>
/// <para>
/// Every test drives raw events through <see cref="FakeSystemEventRegistrar"/> and time through
/// <see cref="FakeTimeProvider"/>; nothing subscribes to the real <c>SystemEvents</c> or
/// <c>NetworkChange</c>. Each public event is stamped with the fake clock's time, so assertions are
/// about exact firing instants, not merely counts.
/// </para>
/// <para>
/// <b>"Exactly when the timer fires".</b> On the fake clock, reaching instant T fires every timer due
/// at or before T before any test code runs, so a raw event "at T" is always delivered after a public
/// event due at T. The spec's burst rule makes that order well defined (firing ends the burst; the
/// raw event at T starts the next one), and these tests pin it. The opposite order, a raw event
/// handled while the timer callback is already in flight on a pool thread, cannot be produced on this
/// clock; see the report for why it was judged benign.
/// </para>
/// </remarks>
public sealed class NetworkDebounceCapSpecTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// <b>Protects:</b> the two numbers in the §3 table, read through the constructor production
    /// uses. Every other test here passes the 750 ms window to the test constructor explicitly, so
    /// without this one a change to the production constant would go unnoticed.
    /// <b>Catches:</b> the production debounce or cap drifting from 750 ms / 5 s.
    /// </summary>
    /// <remarks>
    /// The production constructor builds the real registrar, but subscribing happens only in
    /// <c>Start()</c>, which this test never calls. The raw handler is invoked directly by reflection
    /// instead, so nothing touches the real static <c>SystemEvents</c>/<c>NetworkChange</c>. If the
    /// handler is renamed, this fails with "raw handler not found" -- loudly, not vacuously.
    /// </remarks>
    [Fact]
    public void The_production_constructor_uses_a_750_ms_quiet_period_and_a_5_s_cap()
    {
        var time = new FakeTimeProvider();
        using var source = new Win32SystemEventSource(time, NullLogger<Win32SystemEventSource>.Instance);
        var fired = Record(source, time);
        var raw = typeof(Win32SystemEventSource)
            .GetMethod("OnRawNetworkAddressChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("raw handler not found on Win32SystemEventSource");
        void RaiseRaw() => raw.Invoke(source, [null, EventArgs.Empty]);

        // Quiet period.
        RaiseRaw();
        time.Advance(Ms(749));
        Assert.Empty(fired);
        time.Advance(Ms(1));
        Assert.Equal([Ms(750)], fired);

        // Cap: a raw event every 500 ms, starting at t = 1000.
        fired.Clear();
        time.Advance(Ms(250));
        var burstStart = Elapsed(time);
        for (var i = 0; i < 20; i++)
        {
            RaiseRaw();
            time.Advance(Ms(500));
        }

        Assert.Equal(burstStart + TimeSpan.FromSeconds(5), fired.First());
    }

    /// <summary>
    /// <b>Protects:</b> "fires once no further raw event has arrived for this long" for a single raw
    /// event: exactly at 750 ms.
    /// <b>Catches:</b> an off-by-one window (firing at 749, or needing 751), or a debounce whose
    /// first raw event fires immediately (leading edge).
    /// </summary>
    [Fact]
    public void A_single_raw_event_fires_exactly_750_ms_later()
    {
        var (registrar, time, fired, _) = Create();

        registrar.RaiseNetworkAddressChanged();
        Assert.Empty(fired);
        time.Advance(Ms(749));
        Assert.Empty(fired);
        time.Advance(Ms(1));

        Assert.Equal([Ms(750)], fired);
    }

    /// <summary>
    /// <b>Protects:</b> trailing edge -- "each raw event restarts the window".
    /// <b>Catches:</b> a window measured from the first raw event (it would fire at 750).
    /// </summary>
    [Fact]
    public void Each_raw_event_restarts_the_quiet_period()
    {
        var (registrar, time, fired, _) = Create();

        registrar.RaiseNetworkAddressChanged();
        time.Advance(Ms(600));
        registrar.RaiseNetworkAddressChanged();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal([Ms(1350)], fired);
    }

    /// <summary>
    /// <b>Protects:</b> the 5 s cap: a link flapping faster than the quiet period still produces an
    /// event 5 s after it started changing.
    /// <b>Catches:</b> the uncapped debounce that bs-u4a item 2 replaced -- it would never fire while
    /// the flap continues -- and a cap measured from the latest raw event rather than the first.
    /// </summary>
    [Fact]
    public void A_link_flapping_faster_than_the_quiet_period_fires_exactly_5_s_after_the_first_raw_event()
    {
        var (registrar, time, fired, _) = Create();

        for (var t = 0; t < 5000; t += 500)
        {
            registrar.RaiseNetworkAddressChanged();
            time.Advance(Ms(500));
        }

        Assert.Equal([TimeSpan.FromSeconds(5)], fired);
    }

    /// <summary>
    /// <b>Protects:</b> the exact boundary between the quiet period and the cap. A raw event every
    /// 700 ms keeps the burst open; the last one lands at <paramref name="lastRawMs"/>. Its trailing
    /// deadline is <c>lastRaw + 750</c>, and the cap is 5000; the event fires at whichever is sooner.
    /// <b>Catches:</b> <c>&lt;</c>/<c>&lt;=</c> mistakes at the cap (firing at 5001 when trailing and
    /// cap are within a millisecond), and a cap that overrides a trailing deadline which was already
    /// sooner (firing at 5000 instead of 4999).
    /// </summary>
    [Theory]
    [InlineData(4249, 4999)]
    [InlineData(4250, 5000)]
    [InlineData(4251, 5000)]
    [InlineData(4949, 5000)]
    public void The_event_fires_at_the_sooner_of_the_trailing_deadline_and_the_cap(int lastRawMs, int expectedFireMs)
    {
        var (registrar, time, fired, _) = Create();

        for (var t = 0; t <= 4200; t += 700)
        {
            AdvanceTo(time, t);
            registrar.RaiseNetworkAddressChanged();
        }

        AdvanceTo(time, lastRawMs);
        registrar.RaiseNetworkAddressChanged();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal([Ms(expectedFireMs)], fired);
    }

    /// <summary>
    /// <b>Protects:</b> "firing ends it, so the next raw event starts a new burst", for a raw event at
    /// the very instant the public event fires. Here the timer is due at 4950 (the raw event at 4200
    /// plus 750), and a raw event arrives at 4950.
    /// <b>Catches:</b> a source that does not clear the burst when it fires. The raw event at 4950
    /// would then be judged against the OLD burst's cap (5000) and fire a second event 50 ms after the
    /// first, instead of starting a new quiet period that ends at 5700.
    /// </summary>
    [Fact]
    public void A_raw_event_at_the_instant_the_event_fires_starts_a_new_burst()
    {
        var (registrar, time, fired, _) = Create();

        for (var t = 0; t <= 4200; t += 700)
        {
            AdvanceTo(time, t);
            registrar.RaiseNetworkAddressChanged();
        }

        AdvanceTo(time, 4950); // the timer fires here
        Assert.Equal([Ms(4950)], fired);
        registrar.RaiseNetworkAddressChanged(); // ... and a raw event arrives at the same instant

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal([Ms(4950), Ms(5700)], fired);
    }

    /// <summary>
    /// <b>Protects:</b> "the next raw event starts a new burst with a fresh 5 s", under a flap that
    /// never stops.
    /// <b>Catches:</b> a cap remembered across bursts (every event after the first would fire at once,
    /// one per raw event -- a probe storm on a struggling link), and a cap that is never re-armed
    /// (the second and later events would never come).
    /// </summary>
    [Fact]
    public void A_flap_that_never_settles_fires_once_every_5_s()
    {
        var (registrar, time, fired, _) = Create();

        for (var t = 0; t < 20_000; t += 500)
        {
            AdvanceTo(time, t);
            registrar.RaiseNetworkAddressChanged();
        }

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20)],
            fired);
    }

    /// <summary>
    /// <b>Protects:</b> all three rules at once, against an oracle written from the §3 table, over
    /// irregular schedules (gaps of 0 to 1200 ms, including simultaneous raw events and raw events at
    /// the firing instant).
    /// <b>Catches:</b> any combination of state that the hand-written cases above miss -- for
    /// example a burst start that survives a firing only on some paths, or a cap computed from
    /// the wrong raw event after a zero-length gap.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    [InlineData(99999)]
    public void Firing_times_match_the_spec_oracle_for_irregular_schedules(int seed)
    {
        var (registrar, time, fired, _) = Create();
        var random = new Random(seed);
        var raws = new List<int>();
        var t = 0;
        for (var i = 0; i < 300; i++)
        {
            t += random.Next(0, 1201);
            raws.Add(t);
        }

        foreach (var r in raws)
        {
            AdvanceTo(time, r);
            registrar.RaiseNetworkAddressChanged();
        }

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(Oracle(raws), fired.Select(f => (int)f.TotalMilliseconds).ToList());
    }

    /// <summary>
    /// The §3 table as an executable model, independent of the implementation. A timer due at or
    /// before a raw event's instant fires first (that is what reaching that instant on a clock means),
    /// which ends the burst; the raw event then starts a new one.
    /// </summary>
    private static List<int> Oracle(IReadOnlyList<int> raws)
    {
        const int quietMs = 750;
        const int capMs = 5000;
        var events = new List<int>();
        int? burstStart = null;
        int? due = null;

        foreach (var r in raws)
        {
            if (due is { } d && d <= r)
            {
                events.Add(d);
                burstStart = null;
                due = null;
            }

            burstStart ??= r;
            due = Math.Min(r + quietMs, burstStart.Value + capMs);
        }

        if (due is { } last)
        {
            events.Add(last);
        }

        return events;
    }

    private static (FakeSystemEventRegistrar Registrar, FakeTimeProvider Time, List<TimeSpan> Fired, Win32SystemEventSource Source) Create()
    {
        var registrar = new FakeSystemEventRegistrar();
        var time = new FakeTimeProvider();

        // maxNetworkChangeDeferral deliberately omitted: the 5 s cap under test is the production default.
        var source = new Win32SystemEventSource(registrar, time, Quiet, NullLogger<Win32SystemEventSource>.Instance);
        source.Start();
        return (registrar, time, Record(source, time), source);
    }

    private static List<TimeSpan> Record(Win32SystemEventSource source, FakeTimeProvider time)
    {
        var fired = new List<TimeSpan>();
        source.NetworkAddressChanged += (_, _) => fired.Add(Elapsed(time));
        return fired;
    }

    private static TimeSpan Elapsed(FakeTimeProvider time) => time.GetUtcNow() - DateTimeOffset.UnixEpoch;

    private static void AdvanceTo(FakeTimeProvider time, int milliseconds) =>
        time.Advance(Ms(milliseconds) - Elapsed(time));

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
