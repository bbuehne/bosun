using Bosun.Repair;

namespace Bosun.Tests.UI.Tray.Fakes;

/// <summary>Records the error dialogs it is asked to show; never shows one. Confirmations are not
/// expected by what uses it, so asking for one fails the test.</summary>
internal sealed class FakeRepairPrompt : IRepairPrompt
{
    public List<(string Title, string Message)> Errors { get; } = [];

    /// <summary>When set, <see cref="ShowError"/> throws it after recording nothing -- for the case
    /// where the dialog path itself is broken.</summary>
    public Exception? ShowErrorException { get; set; }

    public Task<RepairConfirmation> ConfirmAsync(string title, string message) =>
        throw new InvalidOperationException("This caller never asks for a confirmation.");

    public void ShowError(string title, string message)
    {
        if (ShowErrorException is not null)
        {
            throw ShowErrorException;
        }

        Errors.Add((title, message));
    }
}
