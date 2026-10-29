# AI Reviewer — Architecture

2026-10-29 · Szymon

## Overview

A local-first, per-person AI code reviewer: a .NET engine on the user's machine polls GitHub and GitLab, runs an AI agent (subscription CLI first, API keys second) on the PR delta, and acts on the findings through a deterministic policy engine.

Decisions taken:

- **Internal tool**, developed in a public repository on the `tez-Szymon` GitHub account, all rights reserved (no open-source license). Not a commercial product.
- **Local only.** The core runs on the user's computer. No cloud component. If the machine is off, nothing is reviewed.
- **Per person.** Each user has their own config, tokens and subscriptions. A team that wants a shared reviewer runs it under a dedicated account.
- **Personal access token** posts reviews. Whether it belongs to a person or a reviewer account is the user's choice.
- **User owns risk.** Auto-approve, request changes and auto-merge are allowed when the user enables them.
- **GitHub and GitLab** in v1, behind one provider abstraction. Self-hosted GitLab is supported from the first GitLab release (M3). GitHub Enterprise Server comes in M3 or later.
- **Subscription CLIs first** (Claude Code, Codex), used on the user's own machine under their own subscription. API keys through the same agent abstraction later.
- **Delta reviews**, preserved across rebases where possible. Re-review on new commits or reassignment is configurable.
- **Desktop UI** with queue, history, findings and settings, built with Avalonia. macOS and Windows in the MVP; Linux desktop after the MVP (the CLI runs on Linux from the start).
- **Built-in instructions** ship with the app and change only through app releases.

The core principle: **the model reports, code decides.** The agent only produces structured findings. Approve, merge and every config-driven behaviour live in code that skills and instructions cannot reach.

## System architecture

One engine process does all the work. The desktop app and the CLI are clients of its local API. The CLI can also host the engine in-process for one-shot runs (`reviewer review <pr-url>`).

Engine pipeline per job:

1. **Scheduler** polls providers, applies trigger rules and enqueues a job keyed by `(repo, PR, head SHA, config hash)`.
2. **Context builder** fetches refs, creates a git worktree, computes the delta and assembles instructions.
3. **Agent runner** calls the selected `IReviewAgent` and validates the findings JSON.
4. **Policy engine** turns findings + config into a decision. No LLM involved.
5. **Publisher** posts the summary and inline comments, sets the decision, merges if allowed, and records state.

Solution layout:

| Project | Responsibility |
|---|---|
| `Reviewer.Core` | Domain model, findings schema, policy engine, config model. No I/O. |
| `Reviewer.Git` | Git CLI wrapper: fetch, worktrees, diffs, ancestry, patch-id |
| `Reviewer.Providers.GitHub` | GitHub REST/GraphQL adapter |
| `Reviewer.Providers.GitLab` | GitLab REST adapter |
| `Reviewer.Agents.Cli` | Claude Code and Codex runners (process + file contract) |
| `Reviewer.Agents.Api` | API runners via `Microsoft.Extensions.AI` |
| `Reviewer.Engine` | Generic Host, scheduler, job queue, pipeline, persistence |
| `Reviewer.Api` | Local HTTP API on a Unix domain socket / named pipe, SSE events |
| `Reviewer.Cli` | `System.CommandLine` client; can host the engine in-process |
| `Reviewer.Desktop` | Avalonia app (MVVM, CommunityToolkit.Mvvm); starts the engine if not running |
| `Reviewer.Instructions` | Built-in review instructions as embedded resources |

`Reviewer.Core` has no dependency on providers, agents or UI. That keeps the policy engine testable with plain unit tests.

## Repository providers

Git operations are shared; the PR/MR layer is per provider behind one interface plus a capabilities object. A `ProviderFactory` resolves the adapter from the repo's host (`github.com`, `gitlab.com`, or a configured self-hosted base URL).

**Shared git layer** (`Reviewer.Git`) shells out to the `git` CLI. LibGit2Sharp has weak worktree support. Operations: shallow-ish fetch of base and head refs, `git worktree add` per job, diff between SHAs, `merge-base --is-ancestor`, `patch-id` for rebase matching.

**Provider interface:**

```csharp
public interface IRepositoryProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<string> GetCurrentUserAsync(CancellationToken ct);
    IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct);
    IAsyncEnumerable<PullRequestRef> ListCandidatesAsync(RepoRef repo, TriggerFilter filter, CancellationToken ct);
    Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct);
    Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct);
    Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct);
    Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, CancellationToken ct);
    Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct);
}
```

`PublishReviewAsync` posts the summary, inline comments and decision together, bound to the reviewed head SHA, so an approval never covers commits pushed after the review. `GetCurrentUserAsync` returns the token owner for the self-PR rule.

`PullRequestSnapshot` carries head/base SHAs, author, assignees, requested reviewers, labels, draft flag, CI state and mergeability. Each adapter maps its own model into it.

Where GitHub and GitLab differ, and the adapter absorbs it:

| Concern | GitHub | GitLab |
|---|---|---|
| Inline comment anchor | `path` + `line` + `side` + `commit_id` | `position` with base, start and head SHAs |
| Approval | Review with state `APPROVE` | Approve endpoint; approval rules may apply |
| Request changes | Review state `REQUEST_CHANGES` | No direct equivalent; unresolved threads or a note |
| Thread resolve | GraphQL `resolveReviewThread` | Discussion `resolved` flag |
| Assigned vs reviewer | Assignees and requested reviewers | Assignees and reviewers |
| Merge | Merge methods per repo | Merge, squash; merge when pipeline succeeds |

`ProviderCapabilities` exposes flags such as `SupportsRequestChanges`, `SupportsThreadResolve`, `SupportsSuggestions`, `SupportsMergeWhenGreen`. The policy engine checks these instead of assuming.

HTTP: a typed `HttpClient` per provider with Polly retry and rate-limit handling. Octokit.net is optional; it lags the API in places. GitHub polling uses ETag / `If-None-Match`; 304 responses don't count against the rate limit.

The token owner can't approve their own PR on either platform. The engine detects `author == token owner` and skips or downgrades to comment-only.

## AI agents

Every backend implements one interface and returns the same validated findings. CLI agents get their inputs as files in a job folder. Output comes from the CLI's native structured-output mode where available, with a `findings.json` written by the agent as the fallback. Either way, the engine validates the same schema before acting.

```csharp
public interface IReviewAgent
{
    string Id { get; }                       // "claude-code", "codex", "anthropic-api"...
    AgentCapabilities Capabilities { get; }  // NeedsWorktree, MaxConcurrency, SupportsSchemaFlag
    Task<AgentHealth> CheckAsync(CancellationToken ct);  // installed? logged in? version?
    Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct);
}
```

### CLI file contract

The job folder lives **outside** the worktree, so the agent's output can't be confused with repo files and the PR can't pre-plant a fake result.

```text
~/.reviewer/jobs/<job-id>/<attempt>/
  context.json      # PR metadata, delta range, changed files, rules summary (input)
  delta.patch       # the diff to review (input)
  instructions.md   # assembled instruction layers (input)
  schema.json       # findings JSON schema (input)
  output.json       # captured structured output from stdout or the CLI's output-file flag
  findings.json     # fallback only: written by the agent when no native mode exists
  agent.log         # captured stdout/stderr
```

Run steps:

1. Create the worktree at the PR head. Write the inputs into a fresh attempt folder, so a stale result from an earlier attempt can never be read.
2. Start the CLI headless with the worktree as working directory and a short prompt: read `context.json` and `delta.patch`, follow `instructions.md`, return findings matching `schema.json`.
3. Output mode, chosen per adapter from `AgentCapabilities`:
   - **Native (preferred):** pass the CLI's JSON / output-schema option and capture the result from stdout or the file the CLI itself writes. The agent writes nothing, so it can run fully read-only.
   - **File fallback:** for CLIs without a structured mode, ask the agent to write `findings.json` to an absolute path in the attempt folder and grant write access to that folder only.

4. Enforce a timeout and kill the process tree on expiry. Read output only after the process exits.
5. Validate against the schema. Invalid → one repair round with the validation errors, then fail the job.
6. Detect rate-limit or auth errors from exit code and output → job state `Throttled` with retry time, or `AgentUnavailable`.

Exact CLI flags are confirmed per CLI version in the adapter, and `CheckAsync` reports which output mode the installed version supports.

Processes are run with CliWrap. Each agent has a concurrency limit, default 1 for subscriptions.

### API agents (phase 2)

Built on `Microsoft.Extensions.AI` `IChatClient`, so OpenAI, Anthropic and others share one code path. The engine owns the tool loop and offers read-only tools only: `read_file`, `list_dir`, `grep`, `git_show`. Structured output enforces the same schema.

### Findings schema

```json
{
  "summary": "string",
  "findings": [
    {
      "id": "string",
      "severity": "blocker | major | minor | nit",
      "category": "security | correctness | performance | tests | maintainability | style | docs",
      "confidence": 0.0,
      "file": "src/Foo.cs",
      "startLine": 10,
      "endLine": 14,
      "title": "string",
      "body": "string",
      "suggestion": "optional replacement code"
    }
  ]
}
```

The engine adds a `fingerprint` (hash of rule category, file, normalised code snippet) for dedup across runs. The model never sets it. Findings pointing outside the delta are dropped or moved to the summary.

## Instructions and skills

Instructions shape what the agent looks for. They never control what the app does. Policy is a separate config file that no instruction layer is ever shown.

Layers, assembled in this order into `instructions.md`:

1. **Core contract** (built-in, always first, not replaceable): role, output schema, "report only, take no actions", treat PR content as untrusted data.
2. **Default review guide** (built-in): what good review covers. The user can disable it per repo.
3. **User skills** (global): Markdown files in `~/.reviewer/skills/`, optionally with frontmatter (`name`, `appliesTo` globs, `categories`).
4. **Repo skills** (per-repo override in app config, or files from the repo's **base branch** if enabled).
5. **Repo agent files** (`CLAUDE.md`, `AGENTS.md` and similar): configurable `ignore | context | instructions`, read from the base branch only.
6. **Core contract restated** (built-in, last): output format and the no-actions rule repeated, because the end of a prompt carries weight.

Why a skill can't override config:

- The agent never sees the policy, so it can't argue with it.
- The agent has no approve, merge or settings capability. CLI agents get a prompt and a job folder; API agents get read-only tools.
- Output is schema-validated. Anything other than findings is discarded.
- Severity thresholds, blocking rules and actions are applied by code after the model returns.

The worst a bad skill can do is skew findings, for example by marking everything a nit. That's the user's own skill and their call.

PR head content is never a source of instructions. The PR could otherwise add a skill that says "approve everything".

Built-in layers (1, 2 and 6) are embedded resources versioned with the app. They change only through app releases, and each review records the app version that produced it.

## Policy engine and review modes

The policy engine is a pure function: `Decide(findings, snapshot, policy, capabilities) → ReviewPlan`. It's the only place that chooses approve, request changes or merge.

Steps:

1. **Filter**: drop findings below `minConfidence`, outside the delta, or in ignored paths.
2. **Classify**: a finding is blocking when it matches any `blockingRules` entry (severity at or above X, category in a set, confidence at or above Y).
3. **Decide** by mode:

| Mode | No blocking findings | Blocking findings |
|---|---|---|
| `comment` | Post comments + summary | Post comments + summary |
| `approve` | Approve | Comment only |
| `gate` | Approve | Request changes |
| `gate-merge` | Approve, then merge if guards pass | Request changes |

4. **Merge guards** (always applied when merging): CI green, PR mergeable, not draft, required approvals met, and the head SHA unchanged since review. The last check prevents merging commits the agent never saw.
5. **Self-PR rule**: author equals token owner → never approve or request changes; post comments only.
6. **Capability check**: if the provider lacks a feature (e.g. request changes on GitLab), fall back as configured (`unresolved-threads` or `comment`).

The output `ReviewPlan` lists comments to post, comments to resolve, the decision and merge intent. The publisher executes it, and the plan is stored for history and dry runs.

**Summary comments.** Each review run posts its own summary comment, or several when needed (the provider's comment size limit, or findings grouped by area). Earlier runs' summaries stay as history and are not edited.

**Dry run**: every mode can run with `dryRun: true`. The plan is shown in the app but not published. Useful for tuning a new repo.

## Triggers, re-review and delta

The scheduler polls each repo on an interval (default 60 s, with ETags) and creates a job only when trigger rules match and the `(PR, head SHA, config hash)` hasn't been reviewed.

**Initial review triggers** (per repo, any combination): all PRs, assigned to me, review requested from me, has label, authored by allowlist. Filters: skip drafts, target branches, path globs, max diff size.

**Re-review triggers** (configurable):

- `onNewCommits` with a debounce (default 5 min), so a burst of pushes produces one review.
- `onReassignment` and `onReviewRequested`.
- `manual` from the app or CLI.

**Delta strategy.** Per PR the engine stores `lastReviewedHead`, `lastReviewedBase`, the reviewed patch-ids per file, and the fingerprints of published findings.

1. New head contains `lastReviewedHead` (`merge-base --is-ancestor`) → normal push. Delta = `lastReviewedHead..newHead`.
2. Not an ancestor → rebase or force-push. Compute patch-ids for the commits in `base..newHead` and match them to the stored ones. Unmatched commits form the delta. Matched commits are skipped.
3. Patch-ids don't line up (squash, heavy edit) → per-file comparison: diff each file's `base..lastHead` change against its `base..newHead` change. Files whose PR-introduced change is identical are skipped.
4. Everything else → full review. Dedup still prevents duplicate comments.

The agent always gets the whole PR checkout, so it can read surrounding code. Only the review target is narrowed to the delta.

**Comment lifecycle on re-review**: a published finding whose fingerprint doesn't reappear and whose lines changed is resolved with a short note. A fingerprint that reappears isn't posted again.

## Configuration

One global policy, overridable per repo. Overrides are deep-merged: a repo sets only the keys it changes. Config lives in `~/.reviewer/config.yaml`, edited through the app or by hand, and validated against a JSON schema on load.

```yaml
accounts:
  - id: gh-main
    provider: github
    host: https://github.com
    token: keychain:reviewer/gh-main   # secret reference, never the token itself
  - id: gl-work
    provider: gitlab
    host: https://gitlab.company.com
    token: keychain:reviewer/gl-work

agents:
  default: claude-code
  claude-code: { maxConcurrency: 1, timeoutMinutes: 15 }
  codex:       { maxConcurrency: 1, timeoutMinutes: 15 }

defaults:                      # global policy
  triggers:
    initial: [assignedToMe, reviewRequested]
    reReview: { onNewCommits: true, debounceMinutes: 5, onReassignment: true }
    skipDrafts: true
    maxDiffLines: 3000
    ignorePaths: ["**/*.lock", "**/dist/**", "**/*.min.js"]
  review:
    mode: comment              # comment | approve | gate | gate-merge
    dryRun: false
    minConfidence: 0.6
    blockingRules:
      - { minSeverity: major, categories: [security, correctness] }
      - { minSeverity: blocker }
    maxInlineComments: 20
  merge:
    method: squash
    requireGreenCi: true
  instructions:
    useDefaultGuide: true
    skills: ["~/.reviewer/skills/*.md"]
    repoAgentFiles: context     # ignore | context | instructions

repos:
  - account: gh-main
    repo: acme/payments-api
    overrides:
      review: { mode: gate }
      instructions: { skills: ["~/.reviewer/skills/payments/*.md"] }
  - account: gl-work
    repo: platform/billing
    agent: codex
```

Repo selection in the app: browse repos visible to an account (by user, org or group) and tick them, or paste a URL. Both produce an entry under `repos`.

The effective config per job is computed and stored with the review. A policy change does **not** re-review open PRs that already have a review. It applies from the next trigger (new commits, reassignment, or a manual run). PRs never reviewed are picked up under the new policy as usual.

## Local API, desktop and CLI

**Local API.** ASP.NET Core minimal API hosted inside the engine, listening on a Unix domain socket on macOS/Linux and a named pipe or Unix socket on Windows. No TCP port means other users and browser pages can't reach it. Endpoints: jobs (list, get, retry, cancel), PRs (status, history), findings, config (get, validate, save), repos (browse, add), agents (health), plus `/events` as Server-Sent Events for live queue updates. A generated OpenAPI spec feeds a typed client shared by CLI and desktop.

**Engine lifetime.** The desktop app starts the engine as a child process if none is running and offers "start at login" (LaunchAgent on macOS, Startup entry on Windows). Closing the window leaves a tray icon; quitting the app stops the engine unless "keep running" is on.

**Desktop app (Avalonia).**

| Screen | Content |
|---|---|
| Queue | Pending, running, throttled jobs; cancel, retry, run now |
| PRs | Watched PRs with last review state and decision |
| Review detail | Findings list, delta range, policy decision and why, agent log, link to PR |
| History | Past runs, filter by repo, mode, outcome |
| Repos | Browse accounts, add by URL, per-repo overrides |
| Settings | Accounts and tokens, agents and health, global policy, skills |

Stack: Avalonia 11 + CommunityToolkit.Mvvm, a FluentAvalonia-style theme, tray icon via Avalonia's `TrayIcon`. Packaging: macOS `.app` signed and notarized (needs an Apple Developer account), Windows MSIX or Velopack installer. Velopack also handles auto-update on both.

**CLI.** `System.CommandLine`, published single-file per OS.

```text
reviewer status                     # engine + agents health
reviewer review <pr-url> [--dry-run] [--agent codex]
reviewer queue | history | show <job-id>
reviewer repos add <url> | list
reviewer config validate | edit
reviewer serve                      # run the engine headless
```

## Storage, secrets and security

**Storage.** SQLite at `~/.reviewer/reviewer.db` via EF Core. Tables: `Jobs` (state machine: Queued, Running, Throttled, Succeeded, Failed, Cancelled), `PullRequests` (last reviewed head/base, patch-ids), `Findings` (with fingerprint), `PublishedComments` (provider comment id ↔ fingerprint), `PollState` (ETags, last seen). Job folders are pruned after N days; findings stay in the DB.

**Secrets.** Tokens are kept in the OS credential store: macOS Keychain, Windows Credential Manager, libsecret on Linux. Config holds only references. Tokens are never written into job folders, logs or the agent's environment. Git fetches use a credential helper scoped to the engine.

**Security baseline.** Even with user-owned risk, these stay on because they protect the user's own machine:

- PR content is data. Instructions come from app config and the base branch only.
- The agent runs in a worktree with no tokens in its environment. Where the CLI supports it, use read-only or restricted tool permissions and disallow shell commands that run project code (`npm install`, test runners), since a PR can modify them.
- Merge only if the head SHA still equals the reviewed SHA.
- The local API listens on a socket or pipe only, never on TCP.
- PRs from forks or unknown authors: configurable per repo, default `comment` mode.

## MVP milestones

| Milestone | Scope | Exit criterion |
|---|---|---|
| **M1 · Core review loop** | GitHub adapter, git worktrees, Claude Code via file contract. Findings schema and validation, policy engine, comment mode. CLI: `reviewer review <url>`, with dry run and publish. | Useful on 20 real PRs |
| **M2 · Engine, automation** | Engine host, polling with ETags, trigger rules, job queue. SQLite state, delta review incl. rebases, dedup, re-review. Local API with SSE; approve and gate modes, self-PR rule. | Runs unattended a week |
| **M3 · Desktop and GitLab** | Avalonia app: queue, PRs, review detail, history, settings. GitLab adapter incl. self-hosted, Codex agent, OS keychain. `gate-merge` with merge guards, per-repo overrides in UI. | Daily driver on both |
| **M4 · API agents, release** | API agents via `Microsoft.Extensions.AI`, read-only tools. Signed installers, auto-update, start at login. Eval set of PRs with known bugs to track review quality. | Installable by others |

M1 and M2 are the MVP: a working reviewer driven from the CLI. M3 adds the desktop app and GitLab, M4 makes it releasable. Avalonia learning can start in parallel with M2, since the UI only talks to the local API. After the MVP: Linux desktop build, packaged the same way as macOS and Windows.

## Open questions

None open. All earlier questions are settled in the Overview: subscription CLIs on the local machine, self-hosted GitLab in M3, GitHub Enterprise in M3 or later, Linux desktop after the MVP, and a public repo with all rights reserved.
