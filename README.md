# AI Reviewer

A local-first, per-person AI code reviewer. A .NET engine on your machine polls GitHub and GitLab, runs an AI agent (Claude Code or Codex under your own subscription first, API keys later) on the PR delta, and acts on the findings through a deterministic policy engine.

**The model reports, code decides.** The agent only produces structured findings. Approve, request changes and merge are decided by code that skills and instructions cannot reach.

## Status

Design stage. Nothing is implemented yet. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture and the MVP milestones.

| Milestone | Scope |
|---|---|
| M1 · Core review loop | GitHub adapter, worktrees, Claude Code, findings schema, policy engine, `reviewer review <url>` |
| M2 · Engine, automation | Polling, triggers, job queue, SQLite state, delta review, local API |
| M3 · Desktop and GitLab | Avalonia app, GitLab (incl. self-hosted), Codex, OS keychain, `gate-merge` |
| M4 · API agents, release | `Microsoft.Extensions.AI` agents, signed installers, auto-update, eval set |

## License

Copyright © 2026 Szymon Bakunowicz. All rights reserved. This repository is public for visibility only; no license is granted. See [LICENSE](LICENSE).
