namespace Bosun.Watchdog;

/// <summary>
/// Tunables for <see cref="SupervisorWatchdog"/> (bs-6to, ADR-020 Decision 3). Constants with names,
/// not configuration: <c>docs/CONFIG-SCHEMA.md</c> has no watchdog section and this is not a
/// per-user preference. Properties are <c>init</c> so tests can shrink them.
/// </summary>
public sealed record WatchdogOptions
{
    /// <summary>
    /// How long the supervisor loop may go without any sign of life before it is called stalled.
    /// 3 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop stamps <c>LastLoopActivityUtc</c> at the start and end of every action and every time
    /// an rc call or probe returns. So the longest the loop can honestly be silent is the longest
    /// single thing it waits on, not the length of a whole action:
    /// </para>
    /// <list type="bullet">
    /// <item><b>60 s</b> -- <c>mount/mount</c>, the slowest rc call (<c>RcloneClient.MountTimeout</c>).
    /// <c>mount/unmount</c> is 30 s and <c>operations/list</c> (the deep probe) 30 s.</item>
    /// <item><b>30 s</b> -- the reconciliation tick. While the supervisor is started it enqueues an
    /// action every 30 s, so an idle but healthy loop still stamps at least that often.</item>
    /// <item><b>5 s</b> -- drain retries (<c>DrainRetryInterval</c>). Each retry is its own action
    /// with its own rc call, so a drain that keeps failing keeps stamping, and one that is wedged
    /// stops stamping.</item>
    /// </list>
    /// <para>
    /// 3 minutes is 3x the 60 s mount timeout and 6x the reconciliation tick. That margin absorbs a
    /// slow machine, a probe timeout the user has configured above its default, and two slow calls
    /// back to back -- none of which is a stall. Past 3 minutes nothing legitimate remains: every
    /// wait the loop can make is bounded well below it, so silence this long means the loop is
    /// blocked on something unbounded or is gone. Shorter would risk restarting a healthy Bosun
    /// mid-mount; longer would only prolong a real outage (the 2026-09-28 incident ran 2.5 days).
    /// </para>
    /// <para>
    /// A user who sets <c>probe_timeout_seconds</c> beyond roughly 150 s would be inside this
    /// margin. That value is far outside anything the schema's own examples use, so the threshold
    /// does not adapt to it; if it ever matters, the right fix is to derive this from the config.
    /// </para>
    /// </remarks>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How often the watchdog looks. 15 s: detection is at most one interval later than
    /// the threshold, which is noise against 3 minutes, and a check is three field reads.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The most automatic restarts allowed in any <see cref="RestartWindow"/>. ADR-020
    /// Decision 3: 3. Past it a persistent fault would become a restart loop that hides the banner.</summary>
    public int MaxRestartsInWindow { get; init; } = 3;

    /// <summary>The rolling window the restart limit counts over. ADR-020 Decision 3: 1 hour.</summary>
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// If two checks are further apart than this, the machine was asleep or the process was starved,
    /// and the loop's silence over that gap says nothing about it. 4 check intervals.
    /// </summary>
    /// <remarks>
    /// Without this, waking from sleep would restart Bosun every time: the loop's last stamp is hours
    /// old by the wall clock, and the watchdog's timer can fire before the loop's own reconciliation
    /// tick does. On a gap the watchdog restarts its own clock -- silence must then last a full
    /// <see cref="StallThreshold"/> from the moment the watchdog noticed.
    /// </remarks>
    public TimeSpan MaxCheckGap => CheckInterval * 4;
}
