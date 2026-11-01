using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HookLogger;

/// <summary>
/// Summarises hooks.jsonl per ADR-021 assumption. It prints facts and observations, not verdicts:
/// the reviewing agent decides pass or fail from the evidence.
/// </summary>
internal static class Report
{
    private sealed record Rec(int Line, JsonObject Json)
    {
        public string Phase => Str("phase") ?? "";
        public string Event => Str("event") ?? "";
        public string Ts => Str("ts_utc") ?? "";
        public JsonObject? Payload => Parse();
        public string? ClaudeKey
        {
            get
            {
                if (Phase == "begin")
                {
                    return Str("claude_key");
                }

                JsonObject? d = Json["derived"] as JsonObject;
                JsonObject? link0 = (Json["parent_chain"]?["links"] as JsonArray)?.FirstOrDefault() as JsonObject;
                return d?["parent_pid"] is null ? null : $"{d["parent_pid"]}@{link0?["created_utc_100ns"]}";
            }
        }

        public string? Str(string name) => Json[name] is JsonValue v && v.TryGetValue(out string? s) ? s : Json[name]?.ToJsonString();

        private JsonObject? Parse()
        {
            try
            {
                return JsonNode.Parse(Str("stdin_raw") ?? "") as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    internal static int Run(string[] args)
    {
        string path = args.Length > 0 ? args[0] : Program.HooksLogPath;
        var o = new StringBuilder();
        if (!File.Exists(path))
        {
            Console.Out.WriteLine($"No log at {path}");
            return 0;
        }

        var recs = new List<Rec>();
        int bad = 0;
        int n = 0;
        foreach (string line in File.ReadLines(path))
        {
            n++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject obj)
                {
                    recs.Add(new Rec(n, obj));
                }
            }
            catch (JsonException)
            {
                bad++;
            }
        }

        Header(o, "OVERVIEW");
        o.AppendLine($"log: {path}");
        o.AppendLine($"lines parsed: {recs.Count}, unparsable: {bad}");
        if (recs.Count > 0)
        {
            o.AppendLine($"first: {recs[0].Ts}   last: {recs[^1].Ts}");
        }

        List<Rec> complete = recs.Where(r => r.Phase == "complete").ToList();
        o.AppendLine($"complete hook records: {complete.Count} " +
            $"(SessionStart {complete.Count(r => r.Event == "SessionStart")}, " +
            $"UserPromptSubmit {complete.Count(r => r.Event == "UserPromptSubmit")}, " +
            $"SessionEnd {complete.Count(r => r.Event == "SessionEnd")})");
        o.AppendLine($"logger errors: {recs.Count(r => r.Phase == "logger_error")}");
        foreach (Rec e in recs.Where(r => r.Phase == "logger_error"))
        {
            o.AppendLine($"  line {e.Line}: {e.Str("error")}");
        }

        Timeline(o, recs);
        LogonSessions(o, complete);
        A1SessionEnd(o, recs);
        A2WtEnv(o, complete);
        A3A4Chain(o, complete);
        A5RemoteControl(o, complete);
        A6PluginRoot(o, complete);
        A10ResumeIds(o, complete);
        A11Timing(o, recs);

        Console.Out.Write(o.ToString());
        return 0;
    }

    private static void Header(StringBuilder o, string title)
    {
        o.AppendLine();
        o.AppendLine("=== " + title + " ===");
    }

    private static string Short(string? id) => id is null ? "-" : id.Length > 8 ? id[..8] : id;

    private static string Exe(JsonNode? n) => (n as JsonObject)?["parent_exe"]?.ToString() ?? "-";

    private static void Timeline(StringBuilder o, List<Rec> recs)
    {
        Header(o, "TIMELINE (every record, in order; MARK lines are the steps you announced)");
        foreach (Rec r in recs)
        {
            if (r.Phase == "mark")
            {
                o.AppendLine($"{r.Ts}  ----- MARK: {r.Str("note")}");
                continue;
            }

            if (r.Phase is "tick" or "logger_error")
            {
                continue;
            }

            JsonObject? p = r.Payload;
            string detail = r.Event switch
            {
                "SessionStart" => "source=" + p?["source"],
                "SessionEnd" => "reason=" + p?["reason"],
                _ => string.Empty,
            };
            string term = r.Event == "SessionEnd" && r.Phase == "complete"
                ? " terminal_alive_at_end=" + (r.Json["terminal_alive_at_session_end"]?.ToString() ?? "null")
                : string.Empty;
            string id = Short(p?["session_id"]?.ToString());
            o.AppendLine($"{r.Ts}  {r.Phase,-8} {r.Event,-16} {detail,-22} sid={id} claude={r.ClaudeKey ?? "-"} parent={Exe(r.Json["derived"])}{term}");
        }
    }

    private static void LogonSessions(StringBuilder o, List<Rec> complete)
    {
        Header(o, "LOGON SESSIONS seen by the hook (ADR-021 Decision 5, row 5 relies on these changing at logoff, restart and shutdown)");
        foreach (IGrouping<string, Rec> g in complete.GroupBy(r =>
        {
            JsonObject? t = r.Json["token"] as JsonObject;
            return $"luid={t?["logon_session_luid"]} logon_time={t?["logon_time_utc"]} logon_type={t?["logon_type"]}";
        }))
        {
            o.AppendLine($"{g.Key}   records={g.Count()}   first={g.First().Ts}   last={g.Last().Ts}");
        }
    }

    private static void A1SessionEnd(StringBuilder o, List<Rec> recs)
    {
        Header(o, "ASSUMPTION 1: what SessionEnd reports, and whether the Terminal is visible at that moment");
        List<Rec> starts = recs.Where(r => r.Phase == "complete" && r.Event == "SessionStart").ToList();
        var processes = starts.Where(r => r.ClaudeKey is not null).Select(r => r.ClaudeKey!).Distinct().ToList();
        o.AppendLine($"Claude Code processes that ran SessionStart: {processes.Count}");
        foreach (string key in processes)
        {
            List<Rec> begin = recs.Where(r => r.Phase == "begin" && r.Event == "SessionEnd" && r.ClaudeKey == key).ToList();
            List<Rec> ends = recs.Where(r => r.Phase == "complete" && r.Event == "SessionEnd" && r.ClaudeKey == key).ToList();
            Rec first = starts.First(r => r.ClaudeKey == key);
            string exe = Exe(first.Json["derived"]);
            string? aliveNow = AliveNow(first);
            o.AppendLine($"- claude process {key} ({exe}); first SessionStart {first.Ts}; process alive now: {aliveNow}");
            if (ends.Count == 0)
            {
                o.AppendLine("    no complete SessionEnd record" + (begin.Count > 0 ? $" (but {begin.Count} 'begin' line(s): hook was spawned and did not finish)" : " (no 'begin' line either)"));
            }

            foreach (Rec e in ends)
            {
                o.AppendLine($"    SessionEnd {e.Ts} reason={e.Payload?["reason"]} " +
                    $"terminal_found={e.Json["terminal_found_at_session_end"]} " +
                    $"terminal_alive_at_session_end={e.Json["terminal_alive_at_session_end"]?.ToString() ?? "null"} " +
                    $"wall_ms={e.Json["wall_ms"]}");
            }
        }

        o.AppendLine("Read with the MARK lines in the timeline: a scenario that killed a session with no SessionEnd shows no complete record.");
    }

    private static string? AliveNow(Rec start)
    {
        JsonObject? link0 = (start.Json["parent_chain"]?["links"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (link0?["pid"] is null || link0["created_utc_100ns"] is null)
        {
            return "unknown";
        }

        uint pid = (uint)(int)link0["pid"]!;
        DateTime created = DateTime.Parse(link0["created_utc_100ns"]!.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        bool? alive = Native.IsAlive(pid, created.ToFileTimeUtc());
        return alive?.ToString() ?? "unknown (access denied)";
    }

    private static void A2WtEnv(StringBuilder o, List<Rec> complete)
    {
        Header(o, "ASSUMPTION 2: WT_SESSION and WT_PROFILE_ID reach the hook process");
        foreach (Rec r in complete.Where(r => r.Event == "SessionStart"))
        {
            JsonObject? wt = r.Json["wt_env"] as JsonObject;
            o.AppendLine($"{r.Ts} claude={r.ClaudeKey} WT_SESSION={(wt?["WT_SESSION"] is null ? "ABSENT" : "present")} " +
                $"WT_PROFILE_ID={(wt?["WT_PROFILE_ID"] is null ? "ABSENT" : "present")} " +
                $"all WT_* names: {string.Join(",", wt?.Select(kv => kv.Key) ?? [])}");
        }
    }

    private static void A3A4Chain(StringBuilder o, List<Rec> complete)
    {
        Header(o, "ASSUMPTIONS 3 and 4: parent chain, tab root, stale-parent rule, hook's parent");
        foreach (Rec r in complete.Where(r => r.Event == "SessionStart"))
        {
            JsonObject? d = r.Json["derived"] as JsonObject;
            JsonObject? tok = r.Json["token"] as JsonObject;
            o.AppendLine($"{r.Ts} claude={r.ClaudeKey} elevated={tok?["elevated"]} logon_type={tok?["logon_type"]}");
            o.AppendLine($"    hook <- {string.Join(" <- ", (d?["chain_exe_names"] as JsonArray)?.Select(x => x?.ToString()) ?? [])}   (stop: {d?["stop_reason"]})");
            o.AppendLine($"    hook's parent (assumption 4): {d?["parent_exe"]}   stale link seen: {d?["any_stale_link"]}");
            o.AppendLine($"    terminal found: {d?["terminal_found"]}  pid={d?["terminal_pid"]}  tab_root={d?["tab_root_exe"]} (pid {d?["tab_root_pid"]}) " +
                $"direct child of terminal: {d?["tab_root_is_direct_child_of_terminal"]?.ToString() ?? "n/a"}");
            JsonArray? links = (r.Json["parent_chain"] as JsonObject)?["links"] as JsonArray;
            foreach (JsonNode? ln in links ?? [])
            {
                if (ln is JsonObject l)
                {
                    o.AppendLine($"      pid {l["pid"]} {l["exe_name"]} created={l["created_utc_100ns"]} before_child={l["created_before_child"]?.ToString() ?? "unknown"} " +
                        (l["missing_from_snapshot"] is not null ? "MISSING " : string.Empty) + $"{l["image_path"]}");
                }
            }
        }

        var parents = complete.Select(r => Exe(r.Json["derived"])).Distinct().ToList();
        o.AppendLine($"Distinct hook parent images across all events: {string.Join(", ", parents)}");
    }

    private static void A5RemoteControl(StringBuilder o, List<Rec> complete)
    {
        Header(o, "ASSUMPTION 5 (partial evidence): CLAUDE_CODE_BRIDGE_SESSION_ID in the hook environment");
        o.AppendLine("The docs say this variable is set during an active Remote Control connection. Your phone/app check in the run sheet is the real test.");
        foreach (Rec r in complete)
        {
            string? bridge = (r.Json["claude_env_values"] as JsonObject)?["CLAUDE_CODE_BRIDGE_SESSION_ID"]?.ToString();
            o.AppendLine($"{r.Ts} {r.Event,-16} claude={r.ClaudeKey} source={r.Payload?["source"]?.ToString() ?? "-"} bridge_session_id={(string.IsNullOrEmpty(bridge) ? "(unset)" : bridge)}");
        }
    }

    private static void A6PluginRoot(StringBuilder o, List<Rec> complete)
    {
        Header(o, "ASSUMPTION 6: where the plugin loaded from, and which edit it saw");
        foreach (Rec r in complete.Where(r => r.Event == "SessionStart"))
        {
            string? root = (r.Json["claude_env_values"] as JsonObject)?["CLAUDE_PLUGIN_ROOT"]?.ToString();
            string where = root is null
                ? "(CLAUDE_PLUGIN_ROOT unset)"
                : root.Contains(@"\plugins\cache\", StringComparison.OrdinalIgnoreCase) || root.Contains("/plugins/cache/", StringComparison.OrdinalIgnoreCase)
                    ? "PLUGIN CACHE COPY"
                    : "NOT in the plugin cache (in place)";
            o.AppendLine($"{r.Ts} claude={r.ClaudeKey} plugin_tag={r.Json["plugin_tag"]?.ToString() ?? "(none)"} root={root}  => {where}");
        }
    }

    private static void A10ResumeIds(StringBuilder o, List<Rec> complete)
    {
        Header(o, "ASSUMPTION 10: does 'claude --resume <id>' keep the same session_id");
        var seen = new List<string>();
        foreach (Rec r in complete.Where(r => r.Event == "SessionStart"))
        {
            string? id = r.Payload?["session_id"]?.ToString();
            string? source = r.Payload?["source"]?.ToString();
            string note = source is "resume" or "fork" or "clear"
                ? seen.Contains(id ?? "?") ? "ID SEEN BEFORE (same id kept)" : "ID NEW (never seen earlier in this log)"
                : string.Empty;
            o.AppendLine($"{r.Ts} claude={r.ClaudeKey} source={source} session_id={id}  {note}");
            if (id is not null)
            {
                seen.Add(id);
            }
        }

        foreach (IGrouping<string?, Rec> g in complete.Where(r => r.Event == "SessionStart" && r.ClaudeKey is not null).GroupBy(r => r.ClaudeKey))
        {
            if (g.Count() > 1)
            {
                o.AppendLine($"claude process {g.Key} fired SessionStart {g.Count()} times: " +
                    string.Join("; ", g.Select(x => $"{x.Payload?["source"]}/{Short(x.Payload?["session_id"]?.ToString())}")));
            }
        }

        o.AppendLine("Note: a restored tab must produce a SessionStart with the SAME session_id as the original for Decision 6's confirmation to work.");
    }

    private static void A11Timing(StringBuilder o, List<Rec> recs)
    {
        Header(o, "ASSUMPTION 11 (and bonus timing): SessionEnd budget and hook cold start");
        foreach (Rec r in recs.Where(r => r.Phase == "complete"))
        {
            o.AppendLine($"{r.Ts} {r.Event,-16} startup_ms={r.Json["startup_ms"]} wall_ms={r.Json["wall_ms"]} (framework-dependent .NET, NOT the NativeAOT helper)");
        }

        var slow = recs.Where(r => r.Phase == "tick").ToList();
        if (slow.Count > 0)
        {
            o.AppendLine("Slow-hook runs (--sleep-ms): 'begin' then a 'tick' every 250 ms. The last tick is when Claude Code cancelled it, if no 'complete' follows.");
            foreach (Rec r in recs.Where(r => r.Phase == "tick"))
            {
                o.AppendLine($"  {r.Ts} hook pid {r.Str("pid")} slept_ms={r.Json["slept_ms"]}");
            }
        }
    }
}
