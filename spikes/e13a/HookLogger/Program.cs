using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HookLogger;

/// <summary>
/// E13a spike (bs-vqh.1): records what Claude Code hooks and Windows Terminal actually do.
/// As a hook it never writes to stdout or stderr and always exits 0, so it cannot disturb a session.
/// </summary>
internal static class Program
{
    private const string LoggerVersion = "e13a-1";

    // Relaxed escaping keeps quotes inside stdin_raw readable instead of " noise.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] AlwaysLoggedClaudeValues =
    [
        "CLAUDE_PLUGIN_ROOT", "CLAUDE_PROJECT_DIR", "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_REMOTE",
    ];

    internal static string OutputDirectory =>
        Environment.GetEnvironmentVariable("BOSUN_SPIKE_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BosunSpike");

    internal static string HooksLogPath => Path.Combine(OutputDirectory, "hooks.jsonl");

    private static int Main(string[] args)
    {
        long startedTicks = Stopwatch.GetTimestamp();
        DateTime startedUtc = DateTime.UtcNow;
        try
        {
            if (args.Length > 0 && args[0] == "--report")
            {
                return Report.Run(args.Skip(1).ToArray());
            }

            if (args.Length > 0 && args[0] == "--mark")
            {
                return WriteMark(args);
            }

            RunHook(args, startedTicks, startedUtc);
        }
        catch (Exception ex)
        {
            TryWriteLine(new JsonObject
            {
                ["ts_utc"] = Stamp(DateTime.UtcNow),
                ["phase"] = "logger_error",
                ["logger_version"] = LoggerVersion,
                ["pid"] = Environment.ProcessId,
                ["argv"] = ToArray(args),
                ["error"] = ex.GetType().Name + ": " + ex.Message,
            });
        }

        // A hook must never fail a session, so every path exits 0 (report and mark modes included).
        return 0;
    }

    private static void RunHook(string[] args, long startedTicks, DateTime startedUtc)
    {
        string eventName = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "(none)";
        int sleepMs = IntOption(args, "--sleep-ms");
        string? pluginTag = StringOption(args, "--plugin-tag");

        // Sampled before any other work so startup_ms is the runtime's own start-up cost.
        double startupMs = (startedUtc - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;

        bool early = eventName == "SessionEnd" || sleepMs > 0;
        if (early)
        {
            // Evidence that the hook was spawned at all, even if it is killed before the full record.
            TryWriteLine(new JsonObject
            {
                ["ts_utc"] = Stamp(DateTime.UtcNow),
                ["phase"] = "begin",
                ["event"] = eventName,
                ["pid"] = Environment.ProcessId,
                ["claude_key"] = ProcessFacts.QuickParentKey(),
                ["plugin_tag"] = pluginTag,
                ["sleep_ms_requested"] = sleepMs,
                ["startup_ms"] = Math.Round(startupMs, 1),
            });
        }

        string stdin = ReadStdin(out bool stdinTimedOut);

        if (sleepMs > 0)
        {
            // Lets the maintainer see when Claude Code cancels a slow hook (assumption 11).
            for (int slept = 0; slept < sleepMs;)
            {
                int step = Math.Min(250, sleepMs - slept);
                Thread.Sleep(step);
                slept += step;
                TryWriteLine(new JsonObject
                {
                    ["ts_utc"] = Stamp(DateTime.UtcNow),
                    ["phase"] = "tick",
                    ["event"] = eventName,
                    ["pid"] = Environment.ProcessId,
                    ["slept_ms"] = slept,
                });
            }
        }

        ProcessFacts.Result facts = ProcessFacts.Collect();
        Native.OwnToken token = Native.ReadOwnToken();

        var wt = new JsonObject();
        var claudeNames = new JsonArray();
        var claudeValues = new JsonObject();
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            string key = (string)kv.Key;
            if (key.StartsWith("WT_", StringComparison.OrdinalIgnoreCase))
            {
                wt[key] = kv.Value as string;
            }
            else if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
            {
                // Names only: some CLAUDE_* variables hold credentials.
                claudeNames.Add(key);
            }
        }

        foreach (string key in AlwaysLoggedClaudeValues)
        {
            claudeValues[key] = Environment.GetEnvironmentVariable(key);
        }

        var record = new JsonObject
        {
            ["ts_utc"] = Stamp(DateTime.UtcNow),
            ["phase"] = "complete",
            ["logger_version"] = LoggerVersion,
            ["event"] = eventName,
            ["argv"] = ToArray(args),
            ["plugin_tag"] = pluginTag,
            ["pid"] = Environment.ProcessId,
            ["runtime"] = Environment.Version.ToString(),
            ["stdin_raw"] = RedactPrompt(stdin, out bool redacted),
            ["stdin_prompt_redacted"] = redacted,
            ["stdin_bytes"] = Encoding.UTF8.GetByteCount(stdin),
            ["stdin_timed_out"] = stdinTimedOut,
            ["wt_env"] = wt,
            ["claude_env_names"] = claudeNames,
            ["claude_env_values"] = claudeValues,
            ["cwd"] = Environment.CurrentDirectory,
            ["token"] = new JsonObject
            {
                ["logon_session_luid"] = token.LogonSessionId,
                ["logon_time_utc"] = token.LogonTimeUtc,
                ["logon_type"] = token.LogonType,
                ["terminal_services_session"] = token.TerminalServicesSession,
                ["elevated"] = token.Elevated,
                ["elevation_type"] = token.ElevationType,
                ["error"] = token.Error,
            },
            ["derived"] = facts.Derived,
            ["parent_chain"] = facts.Chain,
            ["startup_ms"] = Math.Round(startupMs, 1),
        };

        if (eventName == "SessionEnd")
        {
            // Assumption 1: can the helper still see its Terminal alive at the moment SessionEnd runs?
            record["terminal_alive_at_session_end"] =
                facts.TerminalPid is uint tp && facts.TerminalCreated is long tc ? Native.IsAlive(tp, tc) : null;
            record["terminal_found_at_session_end"] = facts.TerminalPid is not null;
        }

        record["wall_ms"] = Math.Round(Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds, 1);
        TryWriteLine(record);
    }

    private static int WriteMark(string[] args)
    {
        string note = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "(no note)";
        TryWriteLine(new JsonObject
        {
            ["ts_utc"] = Stamp(DateTime.UtcNow),
            ["phase"] = "mark",
            ["event"] = "MARK",
            ["note"] = note,
        });
        Console.Error.WriteLine($"marked: {note}");
        return 0;
    }

    private static string ReadStdin(out bool timedOut)
    {
        timedOut = false;
        try
        {
            Task<string> read = Task.Run(() =>
            {
                using Stream input = Console.OpenStandardInput();
                using var reader = new StreamReader(input, new UTF8Encoding(false));
                return reader.ReadToEnd();
            });

            if (read.Wait(TimeSpan.FromSeconds(3)))
            {
                return read.Result;
            }

            timedOut = true;
            return string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Keeps the maintainer's prompt text out of the log; everything else stays verbatim.</summary>
    private static string RedactPrompt(string raw, out bool redacted)
    {
        redacted = false;
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj && obj["prompt"] is JsonValue pv && pv.TryGetValue(out string? text))
            {
                obj["prompt"] = $"<redacted {text.Length} chars>";
                redacted = true;
                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return raw;
    }

    private static void TryWriteLine(JsonObject record)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(record.ToJsonString(JsonOptions) + "\n");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                using var fs = new FileStream(HooksLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                // One Write call per record keeps concurrent hooks from interleaving within a line.
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(15);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    internal static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values)
        {
            array.Add(v);
        }

        return array;
    }

    private static string? StringOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntOption(string[] args, string name) =>
        int.TryParse(StringOption(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Max(0, v) : 0;
}
