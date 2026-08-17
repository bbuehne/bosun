namespace Bosun.SystemEventIntegration;

/// <summary>
/// The seam around three .NET/Win32 system event sources (docs/ARCHITECTURE.md §3, bs-0wz):
///
/// <code>
/// Microsoft.Win32.SystemEvents.PowerModeChanged                 -&gt; Suspend / Resume
/// System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -&gt; NetworkAddressChanged
/// Microsoft.Win32.SystemEvents.SessionSwitch (Reason == SessionLock) -&gt; SessionLock
/// </code>
///
/// One Win32 implementation (<see cref="Win32SystemEventSource"/>), one fake for tests
/// (<c>Bosun.Tests.SystemEventIntegration.Fakes.FakeSystemEventSource</c>). No test in the default suite may
/// subscribe to a real system event -- they fire on the OS's own schedule, not the test's, which is
/// the definition of non-deterministic; real-event coverage is a marked
/// <c>[Trait(TestCategories.Category, TestCategories.Integration)]</c> test the human runs
/// deliberately.
/// </summary>
/// <remarks>
/// <para>
/// <b>SessionLock is v1: reserved, no action.</b> The event is wired and raised so a future epic
/// can act on it without touching this seam again, but nothing in this delivery subscribes to it
/// for any purpose beyond logging (see <see cref="SystemEventSupervisorAdapter"/>).
/// </para>
/// <para>
/// <b>Threading.</b> In the real implementation, <see cref="Suspend"/>, <see cref="Resume"/>, and
/// <see cref="SessionLock"/> are raised on <c>SystemEvents</c>' own dedicated thread, not the UI
/// thread -- a subscriber that blocks that thread stalls every other subscriber in the process
/// (there is exactly one such thread per process; <c>SystemEvents</c> is a process-wide, not
/// per-instance, notification source). <see cref="NetworkAddressChanged"/> is raised from whatever
/// thread pool thread <c>NetworkChange</c> uses internally. Subscribers must not assume any
/// particular thread, including the UI thread.
/// </para>
/// </remarks>
public interface ISystemEventSource : IDisposable
{
    /// <summary>The machine is about to suspend (<c>PowerModeChanged</c>, <c>Mode == Suspend</c>).
    /// Subscribers that need to act before the machine actually sleeps have a bounded window to do
    /// so -- see <see cref="SystemEventSupervisorAdapter"/> and Invariant I8.</summary>
    event EventHandler? Suspend;

    /// <summary>The machine has resumed from suspend (<c>PowerModeChanged</c>, <c>Mode ==
    /// Resume</c>).</summary>
    event EventHandler? Resume;

    /// <summary>Exactly one event per settled network transition -- see the real implementation's
    /// remarks for why this is coalesced rather than a raw passthrough of
    /// <c>NetworkChange.NetworkAddressChanged</c>, which fires several times for one physical
    /// transition (adapter down, DHCP, adapter up).</summary>
    event EventHandler? NetworkAddressChanged;

    /// <summary>The interactive session has locked (<c>SessionSwitch</c>, <c>Reason ==
    /// SessionLock</c>). v1: reserved: every subscriber logs and takes no other action.</summary>
    event EventHandler? SessionLock;

    /// <summary>
    /// Subscribes to the underlying event sources. Safe to call once; a second call is a no-op.
    /// Must be called only after whatever reacts to these events (the mount supervisor, via
    /// <see cref="SystemEventSupervisorAdapter"/>) is already running -- an event arriving before
    /// that has nothing to act on. See ADR-012 and <c>Hosting.StartupOrchestrator</c>'s registration
    /// of this class.
    /// </summary>
    void Start();
}
