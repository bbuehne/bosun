# Agent Instructions

All agent operating instructions for this repository live in
[`CLAUDE.md`](CLAUDE.md). Read it first, regardless of which agent you are.

Summary of the non-obvious parts:

- **Beads (`bd`) is the work queue.** Prefix `bs-`. Do not create `TODO.md`,
  `PLAN.md`, or a `plans/` directory — markdown task tracking is forbidden here.
  Run `bd ready` at session start.
- **Graphify** builds the codebase knowledge graph. Consult
  `graphify-out/GRAPH_REPORT.md` before grepping.
- **`docs/DECISIONS.md` is binding.** Ten ADRs record decisions already made and
  the reasoning behind them. Do not silently deviate; propose an amendment.
- **`CLAUDE.md` §2 lists ten invariants.** They are correctness requirements. The
  most important is that a mounted drive letter must never point at an
  unreachable host — it hangs File Explorer process-wide.
- **`CLAUDE.md` §6 defines the agent topology.** The interfacing agent
  orchestrates and validates; implementation is delegated to subagents defined in
  `.claude/agents/`. Subagents do not inherit `CLAUDE.md`, so those definitions
  carry their own copies of the load-bearing rules — keep them in sync.
- **Worktree hazard:** this app mounts real drive letters and writes a real
  Windows Terminal fragment. `dotnet build` and `dotnet test` are safe from a
  worktree; `dotnet run` is not.
