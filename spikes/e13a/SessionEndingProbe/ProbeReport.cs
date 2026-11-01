using System.Globalization;
using System.Text.Json.Nodes;

namespace SessionEndingProbe;

/// <summary>Reads session-ending.jsonl and lays process disappearances against the SessionEnding timestamp.</summary>
internal static class ProbeReport
{
    internal static int Run(string path, bool all)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"No log at {path}");
            return 0;
        }

        var runs = new List<List<JsonObject>>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? o;
            try
            {
                o = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (o is null)
            {
                continue;
            }

            if (o["kind"]?.ToString() == "start" || runs.Count == 0)
            {
                runs.Add([]);
            }

            runs[^1].Add(o);
        }

        Console.WriteLine($"log: {path}   probe runs found: {runs.Count} (showing {(all ? "all" : "the last")})");
        foreach (List<JsonObject> run in all ? runs : runs.TakeLast(1))
        {
            Summarise(run);
        }

        return 0;
    }

    private static DateTime T(JsonObject o) =>
        DateTime.Parse(o["ts_utc"]!.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static void Summarise(List<JsonObject> run)
    {
        Console.WriteLine();
        Console.WriteLine($"=== run started {run[0]["ts_utc"]}, last line {run[^1]["ts_utc"]}, {run.Count} lines ===");

        List<JsonObject> events = run.Where(r => r["kind"]!.ToString() is not ("procs" or "tick")).ToList();
        foreach (JsonObject e in events)
        {
            Console.WriteLine($"{e["ts_utc"]}  {e["kind"]} {e["reason"]}{e["mode"]}{e["note"]}");
        }

        JsonObject? ending = run.FirstOrDefault(r => r["kind"]!.ToString() == "SessionEnding");
        if (ending is null)
        {
            Console.WriteLine("OBSERVATION: no SessionEnding event was logged in this run.");
        }

        DateTime? endingAt = ending is null ? null : T(ending);
        Console.WriteLine();
        Console.WriteLine("Process changes (offset is relative to SessionEnding; negative means BEFORE it):");
        foreach (JsonObject r in run.Where(r => r["kind"]!.ToString() == "procs"))
        {
            string off = endingAt is DateTime ea ? $"{(T(r) - ea).TotalMilliseconds,8:F0} ms" : "      n/a";
            string rem = r["removed"] is JsonArray { Count: > 0 } rm ? "removed " + string.Join(",", rm.Select(x => x?.ToString())) : string.Empty;
            string add = r["added"] is JsonArray { Count: > 0 } ad ? "added " + string.Join(",", ad.Select(x => x?.ToString())) : string.Empty;
            if (rem.Length + add.Length > 0)
            {
                Console.WriteLine($"{r["ts_utc"]}  {off}  {rem} {add}");
            }
        }

        if (endingAt is DateTime at)
        {
            JsonObject? firstGone = run.FirstOrDefault(r => r["removed"] is JsonArray { Count: > 0 } && T(r) >= at.AddMilliseconds(-60000));
            JsonObject? lastBefore = run.LastOrDefault(r => T(r) <= at && r["kind"]!.ToString() is "procs" or "tick");
            Console.WriteLine();
            Console.WriteLine($"SessionEnding logged at {at:O}");
            Console.WriteLine($"Sampler last seen before it: {lastBefore?["ts_utc"]}");
            Console.WriteLine(firstGone is null
                ? "OBSERVATION: no watched process disappeared in this run's log."
                : $"OBSERVATION: first logged disappearance (within 60 s before SessionEnding or later) at {firstGone["ts_utc"]}, " +
                  $"{(T(firstGone) - at).TotalMilliseconds:F0} ms relative to SessionEnding: {string.Join(",", ((JsonArray)firstGone["removed"]!).Select(x => x?.ToString()))}");
            JsonObject last = run[^1];
            Console.WriteLine($"Last line in this run: {last["ts_utc"]} kind={last["kind"]} ({(T(last) - at).TotalMilliseconds:F0} ms after SessionEnding)");
        }
    }
}
