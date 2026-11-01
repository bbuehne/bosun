using System.Text.Json.Nodes;

namespace HookLogger;

/// <summary>Builds the parent-chain evidence for ADR-021 assumptions 2 to 4.</summary>
internal static class ProcessFacts
{
    private const int MaxDepth = 64;
    private const string TerminalExe = "WindowsTerminal.exe";

    internal sealed record Result(JsonObject Chain, JsonObject Derived, uint? TerminalPid, long? TerminalCreated);

    /// <summary>
    /// Cheap "parent PID@creation time" key, used on the early 'begin' line so a hook that is killed before
    /// its full record can still be tied to its Claude Code process. Same format as the full record's key.
    /// </summary>
    internal static string? QuickParentKey()
    {
        Dictionary<uint, Native.ProcEntry> snap = Native.Snapshot();
        if (!snap.TryGetValue((uint)Environment.ProcessId, out Native.ProcEntry self))
        {
            return null;
        }

        Native.ProcInfo info = Native.Query(self.ParentPid);
        return info.CreationFileTime is long t ? $"{self.ParentPid}@{Native.FormatFileTime(t)}" : $"{self.ParentPid}@";
    }

    internal static Result Collect()
    {
        uint self = (uint)Environment.ProcessId;
        Dictionary<uint, Native.ProcEntry> snap = Native.Snapshot();
        Native.ProcInfo selfInfo = Native.Query(self);

        var links = new JsonArray();
        var exeNames = new JsonArray();
        string stopReason = "unknown";
        bool anyStale = false;

        long? childCreated = selfInfo.CreationFileTime;

        // Facts about each accepted (non-stale) ancestor, kept for the derived section.
        var accepted = new List<(uint Pid, string Exe, long? Created)>();

        if (!snap.TryGetValue(self, out Native.ProcEntry selfEntry))
        {
            stopReason = "self_not_in_snapshot";
        }
        else
        {
            uint next = selfEntry.ParentPid;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                if (next == 0)
                {
                    stopReason = "ppid_zero";
                    break;
                }

                if (!snap.TryGetValue(next, out Native.ProcEntry entry))
                {
                    links.Add(new JsonObject
                    {
                        ["pid"] = next,
                        ["missing_from_snapshot"] = true,
                        ["note"] = "parent PID of the previous link is not a running process",
                    });
                    stopReason = "parent_not_running";
                    break;
                }

                Native.ProcInfo info = Native.Query(next);
                bool? createdBeforeChild = info.CreationFileTime is long pc && childCreated is long cc ? pc < cc : null;
                bool stale = createdBeforeChild == false;

                links.Add(new JsonObject
                {
                    ["pid"] = entry.Pid,
                    ["exe_name"] = entry.ExeName,
                    ["image_path"] = info.ImagePath,
                    ["created_utc_100ns"] = info.CreationFileTime is long t ? Native.FormatFileTime(t) : null,
                    ["created_before_child"] = createdBeforeChild,
                    ["stale_parent"] = stale,
                    ["open_error_win32"] = info.Error,
                });

                if (stale)
                {
                    anyStale = true;
                    stopReason = "stale_parent_pid_reused";
                    break;
                }

                exeNames.Add(entry.ExeName);
                accepted.Add((entry.Pid, entry.ExeName, info.CreationFileTime));

                if (entry.ParentPid == entry.Pid)
                {
                    stopReason = "self_parent";
                    break;
                }

                childCreated = info.CreationFileTime;
                next = entry.ParentPid;
            }
        }


        int termIndex = accepted.FindIndex(a => string.Equals(a.Exe, TerminalExe, StringComparison.OrdinalIgnoreCase));
        uint? termPid = null;
        long? termCreated = null;
        JsonObject derived = new()
        {
            ["self_created_utc_100ns"] = selfInfo.CreationFileTime is long sc ? Native.FormatFileTime(sc) : null,
            ["parent_exe"] = accepted.Count > 0 ? accepted[0].Exe : null,
            ["parent_pid"] = accepted.Count > 0 ? accepted[0].Pid : null,
            ["chain_exe_names"] = exeNames,
            ["stop_reason"] = stopReason,
            ["any_stale_link"] = anyStale,
            ["terminal_found"] = termIndex >= 0,
        };

        if (termIndex >= 0)
        {
            termPid = accepted[termIndex].Pid;
            termCreated = accepted[termIndex].Created;
            derived["terminal_pid"] = termPid;
            derived["terminal_created_utc_100ns"] = termCreated is long tc ? Native.FormatFileTime(tc) : null;
            derived["terminal_alive_now"] = termCreated is long tcc ? Native.IsAlive(termPid.Value, tcc) : null;

            // The tab root is the ancestor directly below WindowsTerminal.exe in the chain.
            if (termIndex > 0)
            {
                var root = accepted[termIndex - 1];
                derived["tab_root_pid"] = root.Pid;
                derived["tab_root_exe"] = root.Exe;
                derived["tab_root_is_direct_child_of_terminal"] = true;
                derived["hook_parent_is_tab_root"] = termIndex == 1;
            }
            else
            {
                derived["tab_root_note"] = "Terminal is the hook's direct parent";
            }
        }

        return new Result(new JsonObject { ["links"] = links, ["stop_reason"] = stopReason }, derived, termPid, termCreated);
    }
}
