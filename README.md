# AI Reviewer

A local-first, per-person AI code reviewer. A .NET engine on your machine polls GitHub and GitLab, runs an AI agent (Claude Code or Codex under your own subscription first, API keys later) on the PR delta, and acts on the findings through a deterministic policy engine.

**The model reports, code decides.** The agent only produces structured findings. Approve, request changes and merge are decided by code that skills and instructions cannot reach.

## Status

M1 is done: `reviewer review <url>` runs the whole loop for one GitHub PR with Claude Code. It was tried on 14 real PRs across 5 repositories, public and private, and found a real bug that earlier human and bot reviews had missed. M2 is in progress: `~/.reviewer/config.yaml` now sets the review mode (`comment`, `approve`, `gate`), thresholds, skills and accounts, globally and per repo, every review is recorded in a local SQLite database, a re-review covers only what's new since the last one, resolving the threads of findings that were fixed, and `reviewer serve` works a persistent job queue that `reviewer queue add` fills, while polling the repos under `repos` (with ETags) and queueing reviews when the trigger rules match. While it runs, the engine serves a local API on a Unix socket (`~/.reviewer/engine.sock`) with jobs, PRs, config and agent health, and streams job changes as Server-Sent Events; `reviewer watch` prints them. M3 has started: account tokens can be read from the OS keychain (`keychain:SERVICE/ACCOUNT`), GitLab merge requests are reviewed too, on gitlab.com or a self-hosted instance, in every mode the config sets, Codex can review instead of Claude Code (`agents.default: codex`, a repo's `agent:`, or `--agent codex`), sandboxed to read only the worktree, and `review.mode: gate-merge` merges an approved PR once the merge guards pass, or has the platform merge it when CI passes while CI is still running. The local API now has what the desktop app needs: config saves that keep the file's comments and layout and never overwrite a hand edit, account status and repo browsing, and the watched repos' open PRs. The desktop app (`src/Reviewer.Desktop`, Avalonia 12) has started. It finds the engine or starts `reviewer serve`, and shows the live queue (cancel, retry), the watched PRs, history, and each review's findings, decision and reasons. Its Repos page adds repos by URL or by browsing an account, and sets each repo's overrides; Settings has the accounts and whether their tokens work, agent health and settings, and the global policy and skills. Both edit only the keys that changed in `config.yaml`, so its comments and layout stay. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture and the MVP milestones.

| Milestone | Scope | Status |
|---|---|---|
| M1 · Core review loop | GitHub adapter, worktrees, Claude Code, findings schema, policy engine, `reviewer review <url>` | Done |
| M2 · Engine, automation | Config, polling, triggers, job queue, SQLite state, delta review, local API | Built; exit criterion (a week unattended) pending |
| M3 · Desktop and GitLab | Avalonia app, GitLab (incl. self-hosted), Codex, OS keychain, `gate-merge` | In progress: OS keychain, GitLab, Codex, `gate-merge`, and the desktop app's six screens built; daily-driver exit criterion pending |
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

Needs Claude Code logged in (`claude auth login`), or Codex 0.162 or later (`codex login`) for reviews with `codex`, and a GitHub token from `GITHUB_TOKEN` or `gh auth login`. For GitLab, add an account to `config.yaml` (`provider: gitlab`, plus `host:` for a self-hosted instance) and a token with the `api` scope, from `GITLAB_TOKEN` or a token reference. An account in `config.yaml` can instead point at the OS keychain with `token: keychain:reviewer/gh-main`; add the item with the platform's own tool, which prompts for the token:

```bash
security add-generic-password -s reviewer -a gh-main -U -w               # macOS Keychain
cmdkey /generic:reviewer/gh-main /user:gh-main /pass                     # Windows Credential Manager
secret-tool store --label=reviewer/gh-main service reviewer account gh-main   # Linux (libsecret)
```

```bash
reviewer status                                         # agent health and each account's sign-in
reviewer review https://github.com/acme/api/pull/7 --dry-run   # show the plan only
reviewer review https://github.com/acme/api/pull/7             # post the review; again later: only what's new
reviewer review https://gitlab.com/acme/web/-/merge_requests/3   # a GitLab merge request, with a GitLab account configured
reviewer review <url> --full                            # review the whole PR again
reviewer review <url> --dry-run --agent codex           # review with Codex instead of the configured agent
reviewer review <url> --dry-run --min-confidence 0.4           # see what a lower threshold adds
reviewer queue add <url> [--dry-run] [--full]           # queue a review for the engine
reviewer serve                                          # run the engine: poll repos, work the queue, serve the local API until Ctrl+C
reviewer watch                                          # print job changes as the running engine reports them
reviewer queue                                          # queued, running and throttled jobs
reviewer queue cancel <job-id> | retry <job-id>         # cancel a job, or queue a failed one again
reviewer history --repo acme/api                        # past reviews, newest first
reviewer show 20261030-1200                             # one review with its findings (id or unique start)
reviewer config validate                                # check config.yaml
reviewer config show acme/api                           # the merged settings a review of acme/api uses
```

State lives in `~/.reviewer` (or `REVIEWER_HOME`): `config.yaml`, `reviewer.db` with every job and its findings, `engine.sock` while the engine runs, job folders with the agent's inputs, output and `plan.json`, and one bare repository per reviewed repo. Without `config.yaml` the built-in defaults apply: comment mode, and Markdown skills from `~/.reviewer/skills/*.md`. A minimal config:

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
