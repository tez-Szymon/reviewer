# AI Reviewer

A local-first, per-person AI code reviewer. A .NET engine on your machine polls GitHub and GitLab, runs an AI agent (Claude Code or Codex under your own subscription first, API keys later) on the PR delta, and acts on the findings through a deterministic policy engine.

**The model reports, code decides.** The agent only produces structured findings. Approve, request changes and merge are decided by code that skills and instructions cannot reach.

## Status

M1 is done: `reviewer review <url>` runs the whole loop for one GitHub PR with Claude Code in comment mode, using built-in defaults (config files come in M2). It was tried on 14 real PRs across 5 repositories, public and private, and found a real bug that earlier human and bot reviews had missed. M2 is next. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture and the MVP milestones.

| Milestone | Scope | Status |
|---|---|---|
| M1 · Core review loop | GitHub adapter, worktrees, Claude Code, findings schema, policy engine, `reviewer review <url>` | Done |
| M2 · Engine, automation | Polling, triggers, job queue, SQLite state, delta review, local API | Next |
| M3 · Desktop and GitLab | Avalonia app, GitLab (incl. self-hosted), Codex, OS keychain, `gate-merge` | |
| M4 · API agents, release | `Microsoft.Extensions.AI` agents, signed installers, auto-update, eval set | |

## Build

Requires the .NET 10 SDK.

```bash
dotnet build Reviewer.slnx
dotnet test Reviewer.slnx
dotnet run --project src/Reviewer.Cli -- --help
```

## Usage

Needs Claude Code logged in (`claude auth login`) and a GitHub token from `GITHUB_TOKEN` or `gh auth login`.

```bash
reviewer status                                         # agent and GitHub account
reviewer review https://github.com/acme/api/pull/7 --dry-run   # show the plan only
reviewer review https://github.com/acme/api/pull/7             # post the review
reviewer review <url> --dry-run --min-confidence 0.4           # see what a lower threshold adds
```

State lives in `~/.reviewer` (or `REVIEWER_HOME`): job folders with the agent's inputs, output and `plan.json`, and one bare repository per reviewed repo. Markdown skills in `~/.reviewer/skills/*.md` are added to every review.

## License

Copyright © 2026 Szymon Bakunowicz. All rights reserved. This repository is public for visibility only; no license is granted. See [LICENSE](LICENSE).
