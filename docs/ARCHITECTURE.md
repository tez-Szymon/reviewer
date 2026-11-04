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
| `Reviewer.Agents.Api` | API agents over `Microsoft.Extensions.AI` `IChatClient`, with the engine's own tool loop and read-only worktree tools; `anthropic-api` |
| `Reviewer.Engine` | Generic Host, scheduler, job queue, pipeline, persistence |
| `Reviewer.Api` | Local HTTP API on a Unix domain socket, SSE events |
| `Reviewer.Api.Client` | The API's contract (request and response types) and `EngineClient`, without ASP.NET Core; shared by the engine, CLI and desktop |
| `Reviewer.Cli` | `System.CommandLine` client; can host the engine in-process |
| `Reviewer.Desktop` | Avalonia app (MVVM, CommunityToolkit.Mvvm); starts the engine if not running; Velopack updates and start at login |
| `Reviewer.Instructions` | Built-in review instructions as embedded resources |

`Reviewer.Core` has no dependency on providers, agents or UI. That keeps the policy engine testable with plain unit tests.

## Repository providers

Git operations are shared; the PR/MR layer is per provider behind one interface plus a capabilities object. The adapter comes from the account that serves the repo's host: `AccountConnection` in the CLI opens a `GitHubProvider` or `GitLabProvider` with the account's token, and gives git the clone URL, head ref (`refs/pull/N/head` or `refs/merge-requests/N/head`) and credentials. PR and MR URLs are read against the configured accounts (`PullRequestUrls`), since only the config says which hosts run GitLab. A GitLab project's owner is its full group path, which can nest (`group/subgroup/project`), and a self-hosted instance may be served from a path (`https://example.com/gitlab`).

**Shared git layer** (`Reviewer.Git`) shells out to the `git` CLI. LibGit2Sharp has weak worktree support. Operations: shallow-ish fetch of base and head refs, `git worktree add` per job, diff between SHAs, `merge-base --is-ancestor`, `patch-id` for rebase matching.

**Provider interface:**

```csharp
public interface IRepositoryProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<string> GetCurrentUserAsync(CancellationToken ct);
    IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct);
    Task<PullRequestList> ListOpenPullRequestsAsync(RepoRef repo, string? etag, CancellationToken ct);
    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken ct);
    Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct);
    Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct);
    Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct);
    Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, string note, CancellationToken ct);
    Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct);
}
```

`PublishReviewAsync` posts the summary, inline comments and decision together, bound to the reviewed head SHA, so an approval never covers commits pushed after the review. `GetCurrentUserAsync` returns the token owner for the self-PR rule.

`PullRequestSnapshot` carries head/base SHAs, author, assignees, requested reviewers, labels, draft flag, CI state and mergeability. Each adapter maps its own model into it.

Where GitHub and GitLab differ, and the adapter absorbs it:

| Concern | GitHub | GitLab |
|---|---|---|
| Review | One review: body, inline comments and event together | Diff discussions, then the summary as a note, then the approval |
| Inline comment anchor | `path` + `line` + `side` + `commit_id` | `position` with the base, start and head SHAs of the MR version at the reviewed head; an unchanged line needs its old line number too |
| Comment off the diff | The whole review is refused; it's posted again with every finding in the body | Checked against the version's diff before posting; that finding goes to the summary |
| Approval | Review with state `APPROVE` | `approve` with the reviewed SHA; skipped if this account already approved |
| Request changes | Review state `REQUEST_CHANGES` | None. The policy falls back, and an earlier approval by this account is withdrawn (`unapprove`) |
| Thread resolve | GraphQL `resolveReviewThread` | Discussion `resolved` flag |
| Suggestions | ` ```suggestion ` over the commented range | ` ```suggestion:-N+0 ` counted from the anchored line |
| Assigned vs reviewer | Assignees and requested reviewers | Assignees and reviewers |
| CI | Combined status and check runs | The head pipeline's status |
| Merge | Merge methods per repo, refused unless the head is the reviewed SHA | The project's merge method; only squashing is per merge. Refused unless the head is the reviewed SHA |
| Merge when green | GraphQL `enablePullRequestAutoMerge` with the reviewed head; needs "Allow auto-merge" on the repo, and waits only for the checks branch protection requires | `auto_merge` (and `merge_when_pipeline_succeeds` for instances before 17.11) with the reviewed SHA |
| Mergeable | `mergeable` (conflicts); null while GitHub computes it counts as not mergeable | `detailed_merge_status` is `mergeable`, or only CI or approvals are missing (`ci_still_running`, `ci_must_pass`, `not_approved`) |

`ProviderCapabilities` exposes flags such as `SupportsRequestChanges`, `SupportsThreadResolve`, `SupportsSuggestions`, `SupportsMergeWhenGreen`. The policy engine checks these instead of assuming.

HTTP: a typed `HttpClient` per provider with Polly retry and rate-limit handling. Octokit.net is optional; it lags the API in places. GitHub polling lists a repo's open PRs most recently updated first, sending the first page's ETag as `If-None-Match`; any change to a PR moves it to that page, so a 304 means nothing changed, and 304 responses don't count against the rate limit. The list carries everything the trigger rules read (head, base branch, author, assignees, requested reviewers, labels, draft flag), so polling needs no per-PR requests; `GetChangedFilesAsync` is called only when a review is about to be queued, for the size and path filters. GitLab polling sends the ETag too, but gitlab.com answers with the full list rather than a 304, so each poll reads the list; `pollSeconds` is the lever there.

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

**Claude Code** (verified on 2.1.312) runs as `claude -p … --restricted --tools Read,Grep,Glob --strict-mcp-config --add-dir <attempt> --output-format json --json-schema <schema> --no-session-persistence`, with the worktree as working directory. `--restricted` is what makes the PR head safe to open: no code-running tools, user/project/local settings files ignored (so a PR can't add hooks through `.claude/settings.json`), the worktree's `CLAUDE.md` not loaded, and file tools confined to the working directories. `CheckAsync` refuses a CLI without it. `--bare` would also skip `CLAUDE.md`, but it limits auth to API keys, so subscriptions couldn't use it. Findings arrive in the result's `structured_output`. The CLI's schema validator rejects a draft 2020-12 `$schema` key, so the runner strips it. The repair round is a fresh call, not a resumed session, so reviews don't fill the user's session history.

**Codex** (verified on 0.162.1) runs as `codex exec --ignore-user-config --ignore-rules --ephemeral --skip-git-repo-check --json -c default_permissions="reviewer" -c permissions.reviewer.filesystem={…} -c permissions.reviewer.network.enabled=false -c project_doc_max_bytes=0 -c web_search="disabled" -c shell_environment_policy.inherit="core" --disable apps,plugins,hooks,browser_use,computer_use,memories --output-schema <file> --output-last-message <attempt>/output.json -- <prompt>`, with the worktree as working directory. Codex has no file tools without a shell: it reads files by running commands, and given the chance it runs the PR's own code to check a finding. So the safety rests on its sandbox rather than on taking the shell away. The permission profile lets commands read only system files (`:minimal`), the worktree and the attempt folder; they write nothing and reach no network, so a PR's code can't read `~/.ssh` or a token file and plant it in a finding. `--ignore-user-config` drops the user's MCP servers and settings, `project_doc_max_bytes=0` keeps the worktree's `AGENTS.md` out of the prompt, and the disabled features cover ChatGPT apps (connectors), plugins and hooks. Codex rejects an unknown feature name, so a renamed one stops reviews instead of quietly staying on. `CheckAsync` requires the version the profile was verified on and the flags above in `codex exec --help`, and reads `codex login status` (exit 0 when signed in). Strict structured output needs every schema property listed as required, so the runner writes `codex-schema.json` with the optional `suggestion` (already nullable) made required. Failures come from the JSONL events: a `turn.failed` error, classified by status (401 → `AgentUnavailable`, 429 or "usage limit" → `Throttled`, with "try again in …" turned into a retry time). The repair round can read only the attempt folder.

Processes are run with CliWrap. Each agent has a concurrency limit, default 1 for subscriptions.

### Job queue

The queue is the `Jobs` table, so it survives restarts and the CLI can add to it while the engine runs. `reviewer queue add` (and later the scheduler) inserts a `Queued` job; a second request for a PR that already has a queued job for the same agent and dry-run setting is merged into it, taking the new request's `NotBefore` (a later push extends the debounce, a manual request ends it). The job reads the PR and the config when it starts, not when it's queued.

`QueueWorker` (run by `reviewer serve`) polls the table every 5 s, or at once when woken, and starts a waiting job when:

- it's due: past `NotBefore`, or, for a `Throttled` job, past `RetryAt` (15 min after the throttle when the agent gave no time);
- its agent has a free slot under `maxConcurrency`, counting jobs running in any process;
- no other job for the same PR is running, in any process. `reviewer review` refuses to start on a PR that's being reviewed, too.

A usage limit is the subscription's, so a throttled job pauses its whole agent until its retry time. A job is claimed with a conditional update (`Queued`/`Throttled` → `Running`), so two processes can't both run it, and records the process id running it. Cancelling a waiting job is immediate; cancelling a running one sets `CancelRequested`, which the worker running it acts on. Stopping the engine cancels its running jobs and queues them again. A job left `Running` by a process that died is marked `Failed` when the engine starts (or `reviewer queue` lists), not re-run, since it may have published already; `reviewer queue retry` queues it again. Only one engine runs at a time: `reviewer serve` holds `engine.lock` in the data folder.

### API agents

API agents call a model's API with the user's API key instead of running a CLI under a subscription (`Reviewer.Agents.Api`). They're built on `Microsoft.Extensions.AI` `IChatClient`, so other providers can share the code path. The first is **`anthropic-api`**: Claude through the Anthropic C# SDK (`Anthropic` on NuGet), whose `client.Beta.AsIChatClient(model)` is the chat client. The default model is `claude-opus-5-5`; any current Claude model works (the agent asks for adaptive thinking, which older ones reject).

The engine owns the tool loop (`ChatReview`): no CLI, no shell, no process for the PR's code to reach. The model gets `instructions.md` as the system prompt and `context.json` and `delta.patch` in its first message, and four read-only tools over the worktree (`WorktreeTools`):

| Tool | |
|---|---|
| `read_file` | A file with line numbers, a range of at most 1,000 lines |
| `list_dir` | A directory, directories marked with `/` |
| `grep` | A .NET regex over the checkout's text files, at most 100 matches; 250 ms per line |
| `git_show` | A file at a commit SHA (the base, say), read from the engine's bare repo with `cat-file` |

Every path is relative to the worktree: absolute paths, `~`, `..` and `.git` are refused, and no symbolic link is followed, so a PR can't add a link to `~/.ssh` and have it read. `git_show` takes only hex SHAs and paths that can't be read as options. Files over 2 MB and binary files aren't read, and each answer is cut at 40,000 characters. A failed tool call is answered with the error, for the model to fix; it doesn't fail the review.

Each request sets `ResponseFormat` to the findings schema (structured outputs: `$schema` and numeric bounds removed, since the API doesn't enforce them; the report is validated against the full schema afterwards), `Reasoning` to high effort, and, through `RawRepresentationFactory`, top-level prompt caching (each round resends the conversation) and a server-side refusal fallback to `claude-opus-4-8`, so a request the model's safeguards decline is served by the fallback rather than failing the review. The conversation is only appended to. The loop runs until the model answers without calling tools, for at most 40 tool rounds; after that, tools are set to `none` and the model is asked for its findings. An answer that doesn't validate gets one repair round. A refusal that still stands, or an answer cut off at the 16,000-token output limit, fails the job. Each round's model, finish reason and token usage, and each tool call, go to `agent.log`; the final answer to `output.json`.

The key is `agents.anthropic-api.apiKey`, a reference like an account token's (`env:NAME` or `keychain:SERVICE/ACCOUNT`), or `ANTHROPIC_API_KEY` without one; it's read when a review or a health check needs it and never written anywhere. The reference isn't part of the config hash, since it doesn't change what a review says. `CheckAsync` looks the model up in the Models API (no tokens), which checks the key and the model name. The SDK retries rate limits and overloads itself; a 429 after its retries is `Throttled` (the engine's 15-minute default, since the SDK's exception carries no retry time), 401/403 `AgentUnavailable`, and other API errors fail the job with the API's message.

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

4. **Merge guards** (always applied when merging): CI green, PR mergeable, not draft, and the head SHA unchanged since review. The last check prevents merging commits the agent never saw, and the platform enforces it again, since the merge carries the reviewed SHA. Required approvals are the platform's to enforce when merging. With `merge.requireGreenCi: false` the CI guard is skipped. CI still running is not a failure where the provider `SupportsMergeWhenGreen`: the plan asks the platform to merge the reviewed head once CI passes (`MergeIntent.MergeWhenGreen`), since the same head isn't reviewed again. No CI at all fails the guard; set `requireGreenCi: false` for such repos.
5. **Self-PR rule**: author equals token owner → never approve or request changes; post comments only.
6. **Capability check**: if the provider lacks a feature (e.g. request changes on GitLab), fall back as configured (`unresolved-threads` or `comment`). Either way the plan asks to withdraw an approval this account gave earlier, so a blocked head is never left approved.

The output `ReviewPlan` lists comments to post, comments to resolve, the decision and merge intent (none, merge now, or merge when CI passes). The publisher executes it, and the plan is stored for history and dry runs.

**Merging.** The agent takes minutes, so gate-merge decides on a snapshot read again after the agent finishes, not the one the review started from. The merge goes last, after the approval and the thread resolves are on the PR, with the configured `merge.method` and the reviewed SHA. A refused merge (required approvals missing, auto-merge not allowed on the repo, `rebase` on GitLab) is a warning on a job that still succeeds: the review stands. The job records what the platform accepted.

**Summary comments.** Each review run posts its own summary comment, or several when needed (the provider's comment size limit, or findings grouped by area). Earlier runs' summaries stay as history and are not edited.

**Dry run**: every mode can run with `dryRun: true`. The plan is shown in the app but not published. Useful for tuning a new repo.

## Triggers, re-review and delta

The scheduler (`Scheduler`, run by `reviewer serve` beside the queue worker) polls each repo under `repos` every `triggers.pollSeconds` (default 60 s, at least 15, with ETags) and creates a job only when trigger rules match and the `(PR, head SHA, config hash)` hasn't been reviewed (a succeeded or skipped job, dry runs included). The ETag is ignored when the repo's trigger settings or the token owner changed since the last poll, since the same list may now trigger differently, and saved only once every PR in the list was handled.

Rules (`TriggerRules`, pure) act on **changes**, not on state: each poll compares a PR with what was seen last time (`SeenPullRequests`), so a PR is queued once per event and one nobody touched is left alone, even when its review failed or was cancelled. A PR is seen only while the filters pass (skip drafts, target branches), so a push to a draft is noticed when it's marked ready. A PR is *followed* once it has a succeeded or skipped review:

- Not followed: queue at once when an initial trigger starts to match, or on a push while one matches (which re-checks a PR the size or path filters turned away, or whose review failed).
- Followed: the re-review triggers below decide, whether or not an initial trigger still matches; GitHub drops a review request once the review is posted, so `review-requested` alone could never follow a PR. A PR first seen with a head other than its last reviewed one was pushed to while the engine was stopped.

`paths` and `maxDiffLines` (added plus deleted lines of the whole PR) are checked when a review is about to be queued; a PR they turn away is logged.

**Initial review triggers** (per repo, any combination): all PRs, assigned to me, review requested from me, has label, authored by allowlist. Filters: skip drafts, target branches, path globs, max diff size.

**Re-review triggers** (configurable):

- `onNewCommits` with a debounce (default 5 min), so a burst of pushes produces one review: each push moves the queued job's `NotBefore`.
- `onReassignment` and `onReviewRequested`: the token owner newly among the assignees or requested reviewers. These start at once, ending any debounce.
- `manual` from the app or CLI.

**Delta strategy** (`DeltaPlanner`). After each *published* review the engine stores, per PR, the reviewed head and base, the patch-ids of the PR's commits, and a per-file hash of the PR's change (changed lines only, whitespace collapsed, so a rebase doesn't change it). It also points `refs/reviewer/pr/<n>/reviewed` at the reviewed head in its local repository, so a force-push can't get that commit pruned. Dry runs store nothing.

0. Head unchanged → skip; the agent doesn't run. `--full`, or a config hash different from the last review's, reviews the head again in full.
1. New head contains the last reviewed head → normal push. Delta = `lastReviewedHead..newHead`, limited to files the PR changes, so merging the base branch in doesn't review the base's changes. No PR file changed → skip.
2. Not an ancestor → rebase or force-push. Match the PR's commits (oldest first) to the stored patch-ids. If a leading run matches and everything after it is new, delta = last matched commit (its new SHA) `..newHead`. All matched → skip. Merges or empty commits in the range, or a mixed order, fall through.
3. Per-file: files whose hash equals the stored one are skipped; delta = `mergeBase..newHead` limited to the rest. None left → skip.
4. Nothing matched → full review.

The agent always gets the whole PR checkout, so it can read surrounding code. Only the review target is narrowed. `context.json` names the delta's kind and reason, and the core contract tells the agent that on a re-review the rest was reviewed already.

**Findings across reviews.** A finding posted by a published review (inline or in the summary) stays *open* until a later review resolves it:

- **Fixed** when its lines changed since the head it was reported against (old-side lines of `thatHead..newHead`; a deleted file counts as changed) and the agent didn't report its fingerprint again. After the review is published, its inline thread gets a short reply ("Fixed in `<sha>`…") and is resolved (GitHub: REST reply, GraphQL `resolveReviewThread`). A thread that can't be resolved is a warning, not a failed review; the finding counts as fixed either way. Dry runs list the threads they would resolve.
- **Dismissed** when a person resolved its thread on the PR. A person's call stands: it no longer blocks, even if the agent reports it again, and it isn't posted again.
- **Still open** otherwise, including when reported again. It isn't posted again, and the PR's own comments with our fingerprint marker are checked as well. An open blocking finding keeps blocking under the current policy's filter and blocking rules, wherever it is, so `approve` and `gate` never approve a PR on a delta that left an earlier blocker untouched.

Fixed and dismissed findings are recorded in the database (`ResolvedByJobId`) and counted in the review summary. Findings that were only in a summary have no thread; they're resolved in the database only.

If the head an open finding was reported against is gone, it counts as untouched and stays open.

## Configuration

One global policy, overridable per repo. Config lives in `~/.reviewer/config.yaml`, edited through the app or by hand. Every key is optional: the file is merged onto built-in defaults (embedded in the engine), and each repo's `overrides` are merged onto the result's `defaults`. Maps merge key by key; lists and values replace, so a repo that sets `ignorePaths` gets exactly that list.

Loading is strict. Unknown or misspelt keys (keys are case-sensitive), wrong types, unknown enum values, out-of-range numbers and dangling references are errors, reported as `config.yaml:<line>: <key path>: <problem>`. The typed model and its checks are the schema; there is no separate JSON Schema file. Keys are camelCase and enum values kebab-case. `reviewer config validate` checks the file and `reviewer config show [owner/name]` prints the merged result.

```yaml
accounts:
  - id: gh-main
    provider: github
    host: https://github.com
    token: env:GITHUB_TOKEN    # a reference, never the token itself: env:NAME or keychain:SERVICE/ACCOUNT
  - id: gl-work
    provider: gitlab
    host: https://gitlab.company.com   # https only; gitlab.com when left out; may carry a path
    token: keychain:reviewer/gl-work   # without one, GITLAB_TOKEN

agents:
  default: claude-code
  claude-code: { maxConcurrency: 1, timeoutMinutes: 15, model: opus }
  codex:       { maxConcurrency: 1, timeoutMinutes: 15 }
  anthropic-api: { maxConcurrency: 1, timeoutMinutes: 15, model: claude-opus-5-5, apiKey: keychain:reviewer/anthropic }   # billed to the key; without apiKey, ANTHROPIC_API_KEY

defaults:                      # global policy
  triggers:
    initial: [assigned-to-me, review-requested]   # also all, has-label (labels), authored-by (authors)
    labels: []
    authors: []
    reReview: { onNewCommits: true, debounceMinutes: 5, onReassignment: true, onReviewRequested: true }
    skipDrafts: true
    targetBranches: []         # empty means any
    paths: []                  # only PRs touching these globs; empty means any
    maxDiffLines: 3000         # added + deleted lines of the whole PR
    pollSeconds: 60            # how often reviewer serve polls the repo; at least 15
  review:
    mode: comment              # comment | approve | gate | gate-merge
    dryRun: false
    minConfidence: 0.6
    blockingRules:
      - { minSeverity: major, categories: [security, correctness] }
      - { minSeverity: blocker }
    maxInlineComments: 20
    ignorePaths: ["**/*.lock", "**/dist/**", "**/*.min.js"]
    requestChangesFallback: unresolved-threads    # or comment
  merge:
    method: squash
    requireGreenCi: true
  instructions:
    useDefaultGuide: true
    skills: ["skills/*.md"]    # layer 3; relative to ~/.reviewer, ~ is the home directory
    repoSkills: []             # layer 4, resolved the same way
    repoAgentFiles: context    # ignore | context | instructions

repos:
  - account: gh-main           # optional when only one account is configured
    repo: acme/payments-api
    overrides:
      review: { mode: gate }
      instructions: { repoSkills: ["skills/payments/*.md"] }
  - account: gl-work
    repo: platform/finance/billing   # GitLab: the project's full path, subgroups included
    agent: codex
```

Without accounts in the file there is one implicit github.com account whose token comes from `GITHUB_TOKEN`, `GH_TOKEN` or `gh auth login`, as in M1. A GitLab account without a token reference reads `GITLAB_TOKEN`. A PR in a repo without an entry is reviewed with `defaults` and the account for its host.

Each review's effective config gets a hash of the settings that shape its result: agent, agent settings, `review`, `merge` and `instructions`. Triggers are left out, since they decide whether a review happens, not what it says. The hash is part of the job key `(repo, PR, head SHA, config hash)`.

Repo selection in the app: browse repos visible to an account (by user, org or group) and tick them, or paste a URL. Both produce an entry under `repos`.

**Saving from the app.** People edit `config.yaml` by hand too, so the app never rewrites the whole file. `ConfigEditor` applies edits (set, remove, or add to a list, at a key path as config errors print it: `repos[2].overrides.review.mode`) to the text itself, using the parser's node positions. Each edit rewrites only the span of the node it changes:

- A new value is written in flow style (`{ mode: gate }`, `[a, b]`).
- A new key goes on its own line after its siblings, at their indentation, with missing maps created in block style.
- A removed key or list item takes its lines, end-of-line comment included. The last one leaves `{}` or `[]`, since an empty block would read as null.

Comments, blank lines, quoting and key order elsewhere are untouched, and so are Windows line endings. Strings YAML would read as something else (`"true"`, `"12"`, `"**/dist/**"`) are quoted.

`ConfigFile` saves an edited or replaced file only if it still has the hash the client read, so a hand edit made meanwhile is never overwritten (409 with the current hash). It saves only if the result is valid (422 with the errors), and writes to a temporary file that replaces the old one.

The effective config per job is computed and stored with the review. A policy change does **not** re-review open PRs that already have a review. It applies from the next trigger (new commits, reassignment, or a manual run). PRs never reviewed are picked up under the new policy as usual.

## Local API, desktop and CLI

**Local API.** ASP.NET Core minimal API hosted inside the engine (`Reviewer.Api`), listening on a Unix domain socket, `engine.sock` in the data folder, on every OS (Windows 10 and later support them too). No TCP port means other users and browser pages can't reach it; the socket file is made owner-only (`0600`) on macOS and Linux. `reviewer serve` starts it next to the scheduler and queue worker, replaces a socket file a stopped engine left behind (the engine lock says none is running), and removes it on exit. If the socket can't be opened, for example because the path is too long for one, the engine logs it and reviews without its API.

| Endpoint | |
|---|---|
| `GET /status` | Engine version, process id, start time, data folder |
| `GET /agents` | Each agent's health: installed, logged in, version |
| `GET /queue` | Queued, running and throttled jobs, oldest first |
| `GET /jobs?repo=&number=&limit=` | Past and current jobs, newest first, with finding counts |
| `GET /jobs/{id}` | One job with its findings and the policy's reasons (from the attempt's `plan.json`), by id or unique start |
| `POST /jobs` | Queue a review (`{url, dryRun, full, agent}`), as `reviewer queue add` |
| `POST /jobs/{id}/cancel`, `/retry` | As `reviewer queue cancel` and `retry` |
| `GET /pulls/{owner}/{repo}/{number}?host=` | A PR's last published review, open findings and jobs |
| `GET /config`, `/config/repos/{owner}/{repo}` | The merged config, and a repo's effective settings |
| `POST /config/validate` | Check a `config.yaml` body (`application/yaml` or `text/plain`) without saving it |
| `GET /config/file`, `PUT /config/file` | The file as written with its hash; replace it (`{yaml, baseHash}`) |
| `PATCH /config` | Apply edits (`{baseHash, edits: [{op: set\|remove\|add, path, value}]}`), all or none, keeping comments and layout |
| `GET /accounts` | The configured accounts, with token references only |
| `GET /accounts/{id}/status` | Whether the account's token works, and whose it is |
| `GET /accounts/{id}/repos?owner=` | Repos the account can see, all or one user's, organization's or GitLab group's (subgroups included), marked when watched |
| `GET /pulls?repo=` | Open PRs in watched repos as last polled, with title, author, last review and newest job |
| `GET /events` | Server-Sent Events: a `job` event per change to a job |
| `POST /shutdown` | Stop the engine as Ctrl+C does; the desktop app uses it for an engine it started |
| `GET /openapi/v1.json` | The OpenAPI spec |

Responses are JSON with camelCase keys and kebab-case enum values, as in the config; errors are problem details. The API reads the same database as the CLI and reads the config file on each request, so it shows what the CLI changed and the config the next job will use. Writes wake the queue worker, so a queued, cancelled or retried job is acted on at once instead of at the next poll.

**Events.** Every insert or update of a job, by any process, gives it a new `Version`: SQLite triggers set it to one past the highest, and since SQLite runs one write at a time, versions commit in order. `/events` polls for versions past the last one it sent (every second) and sends each changed job in its latest state, with its version as the event id; a job that changed twice between polls is sent once. A stream starts from now, from `?after=<version>`, or from `Last-Event-ID` when an `EventSource` reconnects, so a reconnect misses nothing. A client opens the stream first, then loads the lists, and keeps the higher version of each job. `reviewer watch` prints the stream.

The account endpoints use the providers `reviewer serve`'s scheduler keeps, one per account, shared one caller at a time. Repo browsing is `ListAccessibleReposAsync`:

- **GitHub:** `user/repos`; for one owner, the token owner's own repos, an organization's, or another user's public ones.
- **GitLab:** `projects?membership=true`; for one owner, a group's projects with its subgroups', or a user's.

The CLI and the desktop app share one client, `EngineClient` in `Reviewer.Api.Client` (behind `IEngineClient`, so view models test against a fake), and the engine's own request and response types, so there is no generated code to keep in step. The OpenAPI spec stays for other clients.

**Engine lifetime.** The desktop app uses an engine already listening on the socket, or starts `reviewer serve` as a child process and waits up to 20 seconds for its API. The CLI is `REVIEWER_CLI` when set, otherwise the one beside the app (the build puts it there), otherwise the first on `PATH`. It runs as `reviewer serve --log <data folder>/engine.log`: the engine appends its own output to the file, with nothing piped to the app, so it can outlive the app.

Quitting the app stops an engine it started: `POST /shutdown`, which requeues a running review as Ctrl+C does, and a kill if the engine hasn't exited within 15 seconds. With "Keep the engine running after quitting" ticked (in the tray menu and the window's status bar, saved in `desktop.json` in the data folder, apart from `config.yaml`), it's left up instead, and the app finds it on the socket the next time it starts. An engine started elsewhere, such as `reviewer serve` in a terminal, always keeps running. If the event stream breaks, the app says it lost the engine, looks for one every 3 seconds, and resumes the stream after the last version it saw. "Start engine" starts one.

**Tray icon.** On macOS and Windows the app has a tray icon (Avalonia's `TrayIcon`; a template image on macOS, which the menu bar tints for light and dark). Its tooltip says what the queue holds ("1 reviewing, 2 queued", "idle") or why there's no engine; its menu opens the window, toggles keep running, and quits. Closing the window hides it to the tray, so the app keeps following the engine; Quit in the tray menu or the app menu quits, and on macOS clicking the Dock icon shows the window again. On Linux, whose trays vary, closing the window still quits until the Linux desktop build.

**Start at login.** An installed app on macOS or Windows offers "Start at login" (status bar and tray menu). It starts the app with `--background`: the tray and the engine, with the window hidden until it's asked for. On macOS it's a LaunchAgent, `~/Library/LaunchAgents/com.github.tez-szymon.reviewer.plist`, that runs `open -a <bundle> --args --background` at login; it isn't loaded when written, which would start a second copy at once. On Windows it's a `Reviewer` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` pointing at Velopack's stub in the install folder, which starts the current version, so it outlives updates; uninstalling removes it. The setting is the entry itself, not a preference in `desktop.json`. A development build doesn't offer it, since it would register the build folder.

**Updates.** The installed app checks the repository's GitHub releases with Velopack's `UpdateManager` when it starts and every 6 hours, downloads a newer release, and shows "Reviewer x.y.z is ready" with Restart to update (in the status bar and tray menu). Each package's channel is its runtime (`osx-arm64`, `osx-x64`, `win-x64`), so an app only sees builds for its own OS and architecture; pre-releases aren't offered. Velopack's apply-on-startup is off, because the engine may be running from the app's folder: an update replaces those files, the CLI included. So Restart to update first stops an engine the app started, even one kept running, or one an earlier run left running from the app's folder (found by the process id `/status` gives and its executable's path), then applies the update and starts the new version, which starts the engine again. A running review is queued again, as with Ctrl+C. Quitting applies a downloaded update after the app exits, unless an engine is left running from the app's folder; then it waits for Restart to update. An engine from elsewhere, such as `reviewer serve` from a source checkout, is left alone. `REVIEWER_UPDATE_URL`, a folder or URL with a release feed, replaces GitHub to try an update before publishing it.

**Desktop app (Avalonia).**

| Screen | Content |
|---|---|
| Queue | Pending, running, throttled jobs; cancel, retry |
| PRs | Watched PRs with last review state and decision |
| Review detail | Findings list, delta range, policy decision and why, agent log, link to PR |
| History | The 500 newest runs, filter by repo and outcome |
| Repos | Browse accounts, add by URL, per-repo overrides |
| Settings | Accounts and tokens, agents and health, global policy, skills |

Each page loads its list once the event stream is open, then applies events over it, the higher version of a job winning, so a change made while the list loads isn't lost. All six screens are built.

Repos and Settings work on `config.yaml` through `GET /config/file` and `PATCH /config`, and load it again each time they're shown, since it may have been edited by hand. Both read one copy of the file (`ConfigDocument`): the merged config and what the file itself sets (`ConfigLoader.ReadSettings`) come from the same text, so they agree with the hash an edit names. Every setting is either set at its level, or inherits: a repo's overrides inherit the merged `defaults`, and a global setting the built-in value. Unticking a set field removes its key; saving sends only the fields that changed. A pasted repo URL is read against the accounts (the longest matching host path wins, for a GitLab served from a path), and a page under the repo, such as a PR, works too. A save refused because the file changed meanwhile reads it again and says so; one that would leave the config invalid shows each error. An invalid file is shown with its problems and can't be edited from the app until it's fixed by hand.

Stack: Avalonia 12 + CommunityToolkit.Mvvm, Avalonia's built-in Fluent theme with the app's own light and dark brushes, tray icon via Avalonia's `TrayIcon`. The app's assembly is `Reviewer.Desktop`, not `Reviewer`, so it can't be confused with the CLI's `reviewer` beside it on file systems that ignore case. Packaging: Velopack (`vpk`, a local dotnet tool). `eng/package.sh <version> <rid>` publishes the app self-contained with the CLI beside it (one runtime for both, so neither needs .NET installed), then packs it: on macOS a `Reviewer.app` (bundle id `com.github.tez-szymon.reviewer`) in a `.pkg` installer and a portable zip, on Windows a `Setup.exe` installing per user with a Start menu shortcut, plus each runtime's update package and feed. macOS builds are signed with a Developer ID, using the hardened-runtime entitlements .NET needs, and notarized; Windows builds are signed by a configurable signing command (`signtool`, Azure Trusted Signing). Without the certificates the packages are built unsigned, with a warning. Pushing a `v*` tag runs `.github/workflows/release.yml`: tests, packages for `osx-arm64`, `osx-x64` and `win-x64`, and one GitHub release with each runtime's channel merged in; [RELEASING.md](RELEASING.md) has the steps and secrets. Updates are full packages, without deltas.

**CLI.** `System.CommandLine`, published single-file per OS.

```text
reviewer status                     # engine + agents health
reviewer review <pr-url> [--dry-run] [--full] [--agent codex]
reviewer queue [add <pr-url> | cancel <job-id> | retry <job-id>]
reviewer watch [--after <version>]  # job changes as the engine reports them
reviewer history | show <job-id>
reviewer repos add <url> | list
reviewer config validate | show [repo]
reviewer serve [--log <file>]       # run the engine headless; --log appends its output to a file
```

## Storage, secrets and security

**Storage.** SQLite at `~/.reviewer/reviewer.db` via EF Core, in WAL mode so the CLI can read while the engine writes. The engine applies pending migrations when it opens the database; migrations live in `Reviewer.Engine/State/Migrations` and are added with the repo's local `dotnet ef` tool. Tables:

- `Jobs`: one row per job folder, and the job queue. State machine Queued, Running, Skipped, Throttled, Succeeded, Failed, Cancelled, plus head/base SHAs, config hash, agent, app version, decision, merge (what gate-merge got the platform to do), summary, error, retry time, not-before time, full and dry-run flags, a cancel request and the process running it. Failed and throttled runs are recorded too. A trigger-maintained `Version` orders changes for the API's events.
- `Findings`: every finding the agent reported, in report order, with what the policy did with it (`inline`, `summary`, `already-posted`, `dropped`), whether it blocked, its fingerprint, and the job that resolved it.
- `PullRequests`: each PR's last *published* review (head, base, job, commit patch-ids, per-file hashes). Dry runs don't move it, since nothing reached the PR.

- `PollStates`: per watched repo, the PR list's ETag, a hash of the trigger settings and token owner it was read with, and when it was last polled.
- `SeenPullRequests`: each open PR as the scheduler last saw it while the filters passed (head, title, author, whether an initial trigger matched, whether the token owner was assigned or requested); closed PRs are forgotten. `GET /pulls` lists them.

Published comments aren't mirrored: each one carries its fingerprint in a hidden marker, and `GetOwnCommentsAsync` reads them back from the provider. Hosts and repo names compare case-insensitively. Job folders are pruned after N days; findings stay in the DB.

**Secrets.** Tokens are kept in the OS credential store: macOS Keychain, Windows Credential Manager, libsecret on Linux. Config holds only references. A `keychain:SERVICE/ACCOUNT` reference names a generic password: service and account on macOS (read with `security find-generic-password`) and libsecret (`secret-tool lookup`), target name `SERVICE/ACCOUNT` in Windows Credential Manager (`CredReadW`). The engine only reads; tokens are added with the platform's own tool, which prompts for them, so they never appear in a process's arguments. A missing item is reported with the command that adds it; it never falls back to `gh auth token`. Tokens are never written into job folders, logs or the agent's environment. Git fetches use a credential helper scoped to the engine.

**Security baseline.** Even with user-owned risk, these stay on because they protect the user's own machine:

- PR content is data. Instructions come from app config and the base branch only.
- The agent runs in a worktree with no tokens in its environment; each agent keeps only its own sign-in variables (`ANTHROPIC_`/`CLAUDE_` for Claude Code, `OPENAI_`/`CODEX_` for Codex). Claude Code gets read-only file tools and no shell. Codex, which reads through a shell and may run the PR's code, runs in a sandbox that can read only the worktree, the attempt folder and system files, write nothing, and reach no network.
- Merge only if the head SHA still equals the reviewed SHA.
- The local API listens on a Unix domain socket only, never on TCP, and only its owner can connect.
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
