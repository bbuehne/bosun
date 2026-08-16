namespace Bosun.Hosting;

/// <summary>
/// Presents ADR-012 Decision 2's catastrophic-failure channel: "Cannot construct the host at
/// all -- no log directory, container fails -&gt; <c>MessageBox</c> naming the reason, then exit."
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately the ONLY channel in the startup contract that blocks. ADR-012 is explicit
/// that blocking is the wrong hammer everywhere else -- a modal at every login trains the user to
/// dismiss it reflexively. It is right here, specifically, because at this point in startup there
/// is no tray icon to hang state on and (per bs-16b, the defect this interface exists to fix) no
/// logger either. A dialog is the only surface that exists.
/// </para>
/// <para>
/// Behind an interface so that no test ever calls the real implementation: a test that pops a
/// modal hangs CI forever, which is worse than the bug this fixes. See
/// <see cref="MessageBoxCatastrophicStartupNotifier"/> for the production implementation and
/// <c>Bosun.Tests.Hosting.Fakes.FakeCatastrophicStartupNotifier</c> for the test double.
/// </para>
/// </remarks>
public interface ICatastrophicStartupNotifier
{
    /// <summary>
    /// Presents <paramref name="reason"/> and <paramref name="exception"/> to the user and blocks
    /// until they dismiss it. Does not exit the process itself -- the caller decides that, so this
    /// method stays trivially fakeable without a test needing to intercept process termination.
    /// </summary>
    void NotifyAndAwaitDismissal(string reason, Exception? exception);
}

