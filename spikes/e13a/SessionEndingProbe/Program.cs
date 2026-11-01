using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SessionEndingProbe;

/// <summary>
/// E13a spike, ADR-021 assumption 8: at logoff and shutdown, does SessionEnding arrive before
/// Windows Terminal starts tearing tabs down? Start this before signing out; read it with --report afterwards.
/// </summary>
internal static class Program
{
    private static readonly string[] Watched = ["WindowsTerminal", "claude", "node"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object Gate = new();
    private static FileStream? log;
    private static volatile bool sessionEndingSeen;

    private static string LogPath
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("BOSUN_SPIKE_DIR") is { Length: > 0 } d
                ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BosunSpike");
            return Path.Combine(dir, "session-ending.jsonl");
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--report")
        {
            return ProbeReport.Run(args.Length > 1 ? args[1] : LogPath, args.Contains("--all"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        log = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        Write(new JsonObject
        {
            ["kind"] = "start",
            ["pid"] = Environment.ProcessId,
            ["tick_count_64_ms"] = Environment.TickCount64,
            ["note"] = "probe started; watching " + string.Join(", ", Watched),
        });

        SystemEvents.SessionEnding += (_, e) =>
        {
            sessionEndingSeen = true;
            Write(new JsonObject { ["kind"] = "SessionEnding", ["reason"] = e.Reason.ToString(), ["cancel_requested"] = e.Cancel });
        };
        SystemEvents.SessionEnded += (_, e) =>
            Write(new JsonObject { ["kind"] = "SessionEnded", ["reason"] = e.Reason.ToString() });
        SystemEvents.SessionSwitch += (_, e) =>
            Write(new JsonObject { ["kind"] = "SessionSwitch", ["reason"] = e.Reason.ToString() });
        SystemEvents.PowerModeChanged += (_, e) =>
            Write(new JsonObject { ["kind"] = "PowerModeChanged", ["mode"] = e.Mode.ToString() });
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Write(new JsonObject { ["kind"] = "ProcessExit" });

        var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Write(new JsonObject { ["kind"] = "CtrlC", ["note"] = "stopped by the user" });
            quit.Set();
        };

        var sampler = new Thread(() => Sample(quit)) { IsBackground = true, Name = "sampler" };
        sampler.Start();

        Console.WriteLine($"SessionEndingProbe running. Logging to {LogPath}");
        Console.WriteLine("Leave this window open, then sign out / restart / shut down as the run sheet says.");
        Console.WriteLine("Press Ctrl+C to stop it cleanly.");
        quit.Wait();
        return 0;
    }

    private static void Sample(ManualResetEventSlim quit)
    {
        Dictionary<string, SortedSet<int>>? previous = null;
        long lastTick = 0;
        var clock = Stopwatch.StartNew();
        while (!quit.IsSet)
        {
            Dictionary<string, SortedSet<int>> now = Snapshot();
            bool changed = previous is null || !Same(previous, now);
            bool heartbeat = clock.ElapsedMilliseconds - lastTick >= 1000;

            // Once SessionEnding has fired, log every 50 ms sample so liveness up to the end is visible.
            if (changed || heartbeat || sessionEndingSeen)
            {
                var rec = new JsonObject
                {
                    ["kind"] = changed ? "procs" : "tick",
                    ["after_session_ending"] = sessionEndingSeen,
                };
                foreach (string name in Watched)
                {
                    rec[name] = new JsonArray(now[name].Select(p => (JsonNode?)p).ToArray());
                }

                if (previous is not null && changed)
                {
                    var added = new JsonArray();
                    var removed = new JsonArray();
                    foreach (string name in Watched)
                    {
                        foreach (int p in now[name].Except(previous[name]))
                        {
                            added.Add($"{name}:{p}");
                        }

                        foreach (int p in previous[name].Except(now[name]))
                        {
                            removed.Add($"{name}:{p}");
                        }
                    }

                    rec["added"] = added;
                    rec["removed"] = removed;
                }

                Write(rec);
                lastTick = clock.ElapsedMilliseconds;
            }

            previous = now;
            Thread.Sleep(50);
        }
    }

    private static Dictionary<string, SortedSet<int>> Snapshot()
    {
        var result = new Dictionary<string, SortedSet<int>>();
        foreach (string name in Watched)
        {
            var set = new SortedSet<int>();
            Process[] procs = Process.GetProcessesByName(name);
            foreach (Process p in procs)
            {
                try
                {
                    set.Add(p.Id);
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }

            result[name] = set;
        }

        return result;
    }

    private static bool Same(Dictionary<string, SortedSet<int>> a, Dictionary<string, SortedSet<int>> b) =>
        Watched.All(n => a[n].SetEquals(b[n]));

    private static void Write(JsonObject record)
    {
        record["ts_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        byte[] bytes = Encoding.UTF8.GetBytes(record.ToJsonString(JsonOptions) + "\n");
        lock (Gate)
        {
            if (log is null)
            {
                return;
            }

            try
            {
                log.Write(bytes, 0, bytes.Length);
                log.Flush(true);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
