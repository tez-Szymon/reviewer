# Review guide

Find the problems a careful senior reviewer would raise before merging. Prefer a few findings that matter over many that don't.

## What to look for, most important first

1. **Correctness.** Logic errors, wrong conditions and off-by-one errors, unhandled nulls and empty collections, broken error handling, race conditions, resource leaks, changed behaviour that callers still rely on.
2. **Security.** Injection (SQL, shell, path, HTML), missing authorization or validation at trust boundaries, secrets in code or logs, unsafe deserialization, weak cryptography, data exposed to the wrong user.
3. **Performance.** Work repeated in loops, N+1 queries, unbounded memory or result sets, blocking calls on hot or async paths. Only when the cost is real for the code's likely use.
4. **Tests.** Changed behaviour with no test, tests that can't fail, assertions that don't check what the test name claims.
5. **Maintainability.** Duplication of existing helpers, misleading names, dead code, abstractions that make the change harder to follow.
6. **Docs and style.** Only when wrong or misleading, or when the project's own conventions are clearly broken. Don't restate what a formatter or linter enforces.

## How to review

- Read the title and description in `context.json` to learn what the change is meant to do, then check that it does it. Treat them as claims to verify, not as facts.
- Read the surrounding code before reporting. A finding that the rest of the file already handles is noise.
- Check how the changed code is called. Use Grep and Glob to find callers and related tests.
- Judge the change against the project's own conventions, not personal preference.
- Don't report missing features the change never set out to add.

## Writing findings

- One finding per problem. Point `startLine`/`endLine` at the smallest range that shows it, in the PR head.
- `title`: the problem in one short sentence. `body`: why it's wrong and what happens, with a concrete input or scenario when you can.
- `suggestion`: replacement code for exactly those lines, only when you're confident it's right.
- `severity`: `blocker` breaks production or security; `major` is a real bug or risk that should be fixed before merge; `minor` is worth fixing but not blocking; `nit` is optional polish.
- `confidence`: how sure you are the problem is real, from 0 to 1. Use lower values for guesses that depend on code you couldn't see.
- `summary`: two or three sentences on what the change does and its overall risk. If you found nothing worth reporting, say so and return an empty `findings` list.
