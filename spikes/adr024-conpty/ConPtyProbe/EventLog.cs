using System.Text.Json;

namespace ConPtyProbe;

/// <summary>Appends one JSON line per event to events.jsonl, with the time and whether the workstation
/// was locked at that moment, and shows the event on the probe's own console (or in its title while the
/// console is mirroring Claude Code's screen).</summary>
internal sealed class EventLog(string path, bool mirroring) : IDisposable
{
    private readonly StreamWriter writer = new(path, append: true) { AutoFlush = true };
    private readonly Lock gate = new();

    public void Write(string name, string detail = "")
    {
        var locked = Native.IsWorkstationLocked();
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.Now.ToString("O"),
            locked,
            @event = name,
            detail,
        });

        lock (gate)
        {
            writer.WriteLine(line);
            var shown = $"[{DateTime.Now:HH:mm:ss}] {name}{(detail.Length > 0 ? ": " + detail : "")}";
            if (mirroring)
            {
                try
                {
                    Console.Title = shown.Length > 200 ? shown[..200] : shown;
                }
                catch (IOException)
                {
                    // No console title to set; the event is still in the log.
                }
            }
            else
            {
                Console.WriteLine(shown);
            }
        }
    }

    public void Dispose() => writer.Dispose();
}
