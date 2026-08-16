using System.Windows;

namespace Bosun.Hosting;

/// <summary>
/// Production implementation of <see cref="ICatastrophicStartupNotifier"/>: a blocking
/// <see cref="MessageBox"/> naming the failure reason and the exception message. See the
/// interface doc comment for why blocking is correct here and nowhere else in the startup
/// contract (ADR-012 Decision 2).
/// </summary>
/// <remarks>
/// Never instantiate this from a test. Every test that needs a notifier uses a fake -- see
/// <c>Bosun.Tests.Hosting.Fakes.FakeCatastrophicStartupNotifier</c>.
/// </remarks>
public sealed class MessageBoxCatastrophicStartupNotifier : ICatastrophicStartupNotifier
{
    public void NotifyAndAwaitDismissal(string reason, Exception? exception)
    {
        MessageBox.Show(
            $"{reason}{Environment.NewLine}{Environment.NewLine}{exception?.Message ?? "(no exception detail available)"}",
            "Bosun failed to start",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}


