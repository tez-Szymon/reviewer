# AI Reviewer

A local-first, per-person AI code reviewer. A .NET engine on your machine polls GitHub and GitLab, runs an AI agent (Claude Code or Codex under your own subscription first, API keys later) on the PR delta, and acts on the findings through a deterministic policy engine.

**The model reports, code decides.** The agent only produces structured findings. Approve, request changes and merge are decided by code that skills and instructions cannot reach.

## Status

M1 is done: `reviewer review <url>` runs the whole loop for one GitHub PR with Claude Code. It was tried on 14 real PRs across 5 repositories, public and private, and found a real bug that earlier human and bot reviews had missed. M2 is in progress: `~/.reviewer/config.yaml` now sets the review mode (`comment`, `approve`, `gate`), thresholds, skills and accounts, globally and per repo, every review is recorded in a local SQLite database, and a re-review covers only what's new since the last one, resolving the threads of findings that were fixed. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture and the MVP milestones.

| Milestone | Scope | Status |
|---|---|---|
| M1 · Core review loop | GitHub adapter, worktrees, Claude Code, findings schema, policy engine, `reviewer review <url>` | Done |
| M2 · Engine, automation | Config, polling, triggers, job queue, SQLite state, delta review, local API | In progress |
| M3 · Desktop and GitLab | Avalonia app, GitLab (incl. self-hosted), Codex, OS keychain, `gate-merge` | |
| M4 · API agents, release | `Microsoft.Extensions.AI` agents, signed installers, auto-update, eval set | |

## Build

Requires the .NET 10 SDK.

```bash
dotnet build Reviewer.slnx
dotnet test Reviewer.slnx
dotnet run --project src/Reviewer.Cli -- --help
```

After changing the database model in `src/Reviewer.Engine/State`, add a migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Reviewer.Engine --startup-project src/Reviewer.Engine --output-dir State/Migrations
```

## Usage

Needs Claude Code logged in (`claude auth login`) and a GitHub token from `GITHUB_TOKEN` or `gh auth login`.

```bash
reviewer status                                         # agent and GitHub account
reviewer review https://github.com/acme/api/pull/7 --dry-run   # show the plan only
reviewer review https://github.com/acme/api/pull/7             # post the review; again later: only what's new
reviewer review <url> --full                            # review the whole PR again
reviewer review <url> --dry-run --min-confidence 0.4           # see what a lower threshold adds
reviewer history --repo acme/api                        # past reviews, newest first
reviewer show 20261030-1200                             # one review with its findings (id or unique start)
reviewer config validate                                # check config.yaml
reviewer config show acme/api                           # the merged settings a review of acme/api uses
```

State lives in `~/.reviewer` (or `REVIEWER_HOME`): `config.yaml`, `reviewer.db` with every job and its findings, job folders with the agent's inputs, output and `plan.json`, and one bare repository per reviewed repo. Without `config.yaml` the built-in defaults apply: comment mode, and Markdown skills from `~/.reviewer/skills/*.md`. A minimal config:

```yaml
defaults:
  review: { minConfidence: 0.5 }
repos:
  - repo: acme/api
    overrides:
      review: { mode: gate }    # approve when nothing blocks, request changes otherwise
```

See [Configuration](docs/ARCHITECTURE.md#configuration) for every key.

## License

Copyright © 2026 Szymon Bakunowicz. All rights reserved. This repository is public for visibility only; no license is granted. See [LICENSE](LICENSE).
