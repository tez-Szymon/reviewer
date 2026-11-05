# Eval set

Changes with known bugs, for tracking how well reviews catch them. `reviewer eval` reviews each case the way a review of a PR runs: same context, instructions, agent and policy filters. Nothing is posted anywhere, and nothing goes into the review history. It then scores which known bugs the findings report.

```bash
reviewer eval                                  # every case in eval/cases, with the configured agent
reviewer eval --case csharp-export-last-page   # one case; repeat --case for more
reviewer eval --agent anthropic-api --model claude-sonnet-5-5
reviewer eval path/to/other/cases
```

The eval uses the global settings from `config.yaml`: the agent and its model, `minConfidence`, `ignorePaths`, the default guide and user skills. Repo overrides don't apply. Each run is saved to `~/.reviewer/evals/results/` as JSON. The output then compares the run with the latest earlier run that used the same agent and model, covering only the cases both runs reviewed. It shows the bugs that are newly caught and the bugs that are newly missed. The eval keeps its repositories and job folders in `~/.reviewer/evals`, so you can read each agent's inputs and output there.

Each case is one review, so a full run costs as much as nine reviews. The fixtures are small and take about 15 seconds each with Claude Code. The real cases are full PRs from this repo's history and take minutes each.

## Scoring

A finding catches a known bug when both are true:

- The finding is in the bug's file.
- Its lines overlap the bug's lines, give or take 3.

Only findings the review would post count: the policy's confidence and path filters apply first. Findings that match no known bug are listed as **other**. Some are noise, and some are real problems the case doesn't list. If a case keeps flagging a real problem, add it to the case's bugs. A case with no bugs (`bugs: []`) measures noise, because any finding on it is one too many.

## Cases

| Case | Kind | Known bug |
|---|---|---|
| `csharp-export-last-page` | fixture | Integer division skips the last, partial page |
| `python-user-search-injection` | fixture | SQL injection through an f-string |
| `ts-save-all-not-awaited` | fixture | `forEach(async …)` resolves before anything is saved |
| `go-config-validation-ignored` | fixture | The validation error is assigned and then dropped |
| `csharp-rate-cache-race` | fixture | A `Dictionary` in a singleton, written by concurrent requests |
| `csharp-clean-extract-method` | fixture | None: a clean refactor, for noise |
| `history-job-id-retry` | this repo, 1a6ab83 | Every constraint error is retried as a duplicate job id (fixed in b4e2505) |
| `rereview-dismissed-blocks` | this repo, 749baef | A dismissed finding blocks again after the dismissal is recorded (fixed in ae29f38) |
| `events-headers-late` | this repo, e3d9556 | The event stream sends no headers until the first event (fixed in 19190eb) |

## Adding a case

Make a folder under `cases/` named for the case, and put a `case.yaml` in it:

```yaml
title: Export orders a page at a time     # shown to the agent as the PR title
description: Optional PR description.
bugs:
  - file: src/Export/OrderExport.cs       # path in the head
    lines: 11-12                          # or a single line, in the head
    about: The last, partial page is never exported.
```

**A fixture** keeps its change beside `case.yaml`:

- `before/` holds the base files. Leave it out if the change only adds files.
- `after/` holds the head files.

The eval commits both folders with a fixed author and date, so every run reviews the same commits. Keep fixtures small, with enough surrounding code for the bug to be visible, and don't hint at the bug in names or comments.

**A real change** names two commits instead of the folders:

```yaml
repo: https://github.com/owner/name.git
base: <full 40-character SHA>    # the commit the PR branched from
head: <full 40-character SHA>    # the PR head with the bug
```

The commits are fetched by SHA without credentials, so the repository must be public. A merged PR with a known bug, plus the later commit that fixed it, makes a good case. Point `lines` at the bug in the head commit, not in the fix.

The tests check that every shipped case loads, and that each fixture bug's lines exist in its file.
