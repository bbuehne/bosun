# Bosun

Windows tray app for managing SSH sessions, Windows Terminal profiles, and
supervised SFTP drive mounts.

> **Warning — this tool mounts network filesystems.**
> Bosun creates and removes drive letters backed by SFTP. When a mount
> misbehaves, Windows File Explorer, file dialogs, and `dir` can hang for
> seconds at a time, in applications unrelated to Bosun. The design goes to
> some lengths to prevent this (mounts are removed proactively when a host stops
> responding), but you should understand the failure mode before installing it.

## What it does

- **Windows Terminal profiles**, generated per host, with per-host tab colours
  and optional tmux auto-attach. Written as a Terminal *fragment*, so your own
  `settings.json` is never touched.
- **Supervised SFTP mounts** via rclone + WinFsp. Each host is independently
  configured as persistent (mounted whenever reachable), on-demand (mounted from
  the tray), or none.
- **Session monitoring** — which hosts have live `ssh.exe` sessions, and their
  connection state.
- **Sleep and network awareness** — mounts are dropped before the machine
  suspends and re-established after it resumes on whatever network it wakes up
  on. Closing the lid at the office and opening it at home should leave nothing
  wedged.

Drive letters mean drag-and-drop with real Explorer windows, in both directions.

## Status

Early. Built for one person's daily workflow and shared in case it is useful to
someone else.

Bug reports are genuinely welcome, particularly from environments the author
cannot reproduce — corporate VPNs, domain-joined machines, docking stations,
bastion hosts. Feature requests that do not serve the author's own use are
likely to be declined; that is not hostility, it is the reason the tool stays
small enough to be reliable.

There is no roadmap and no support commitment.

## Requirements

- Windows 10 (1809+) or Windows 11
- [WinFsp](https://winfsp.dev/)
- [rclone](https://rclone.org/)
- Windows Terminal
- SSH key-based authentication with `ssh-agent` (password and MFA auth are not
  supported — see `docs/DECISIONS.md`, ADR-007)

## Configuration

Copy `config/hosts.example.toml` to `config/hosts.toml` and edit. The schema is
documented in [`docs/CONFIG-SCHEMA.md`](docs/CONFIG-SCHEMA.md).

`config/hosts.toml` is gitignored and never leaves your machine.

## Design

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — components, the mount state
  machine, failure modes
- [`docs/DECISIONS.md`](docs/DECISIONS.md) — why it is .NET and not Python, why
  rclone and not sshfs-win, why mounts are dropped rather than retried

## Why "Bosun"

The boatswain is the crew member responsible for the rigging: inspecting the
lines, replacing them before they part, keeping everything secure while everyone
else gets on with their work.

## Licence

MIT.
