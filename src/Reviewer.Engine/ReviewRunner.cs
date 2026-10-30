using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Engine.State;

namespace Reviewer.Engine;

/// <param name="ConfigHash">From the effective config; recorded with the job.</param>
/// <param name="Full">Review the whole PR, even if an earlier review covered part of it.</param>
public sealed record ReviewRequest(
    PullRequestRef PullRequest,
    ReviewSource Source,
    ReviewPolicy Policy,
    InstructionSettings Instructions,
    TimeSpan Timeout,
    string ConfigHash,
    bool Full = false);

/// <param name="AttemptFolder">Holds the agent's inputs, its output and <c>plan.json</c>; null when skipped.</param>
/// <param name="Published">False for a dry run or a skip.</param>
/// <param name="Skipped">Nothing new since the last review, so the agent wasn't run; <see cref="Delta"/> says why.</param>
/// <param name="Resolved">Earlier findings this review found fixed.</param>
/// <param name="StillOpen">Earlier findings that are still open, whether or not they were reported again.</param>
public sealed record ReviewOutcome(
    string JobId,
    string? AttemptFolder,
    PullRequestSnapshot Snapshot,
    ReviewDelta Delta,
    FindingsReport Report,
    ReviewPlan Plan,
    bool Published,
    bool Skipped = false,
    int Resolved = 0,
    int StillOpen = 0);

/// <summary>
/// One review from start to finish: snapshot, delta, context, agent, policy, publish. The agent's findings reach the PR
/// only through the policy engine's plan. Every run is recorded in the store, including the ones that fail.
/// </summary>
/// <remarks>
/// A re-review covers only what's new, so findings from earlier reviews are tracked here. One whose lines changed and
/// that wasn't reported again is resolved. Any other stays open, isn't posted again, and if it was blocking it keeps
/// blocking: approving needs every blocker fixed, not only the ones in the latest delta.
/// </remarks>
public sealed class ReviewRunner(IRepositoryProvider provider, ReviewContextBuilder builder, IReviewAgent agent, ReviewStore store)
{
    public const string PlanFile = "plan.json";

    private static readonly JsonSerializerOptions PlanJson = new(FindingsJson.Options) { WriteIndented = true };

    /// <exception cref="AgentException">The agent is unavailable, throttled, failed or returned invalid output.</exception>
    public async Task<ReviewOutcome> RunAsync(ReviewRequest request, CancellationToken ct)
    {
        var pr = request.PullRequest;
        var jobId = await store.StartJobAsync(
            new JobRecord
            {
                Id = builder.NewJobId(pr),
                Host = ReviewStore.Host(pr.Repo),
                Repo = pr.Repo.ToString(),
                Number = pr.Number,
                ConfigHash = request.ConfigHash,
                AgentId = agent.Id,
                AppVersion = ReviewStore.AppVersion,
                DryRun = request.Policy.DryRun,
            },
            ct).ConfigureAwait(false);

        try
        {
            return await ReviewAsync(jobId, request, ct).ConfigureAwait(false);
        }
        catch (AgentThrottledException e)
        {
            await FailQuietlyAsync(jobId, JobState.Throttled, e.Message, e.RetryAt).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailQuietlyAsync(jobId, JobState.Cancelled, "Cancelled.", null).ConfigureAwait(false);
            throw;
        }
        catch (Exception e)
        {
            await FailQuietlyAsync(jobId, JobState.Failed, e.Message, null).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ReviewOutcome> ReviewAsync(string jobId, ReviewRequest request, CancellationToken ct)
    {
        var pr = request.PullRequest;
        var snapshot = await provider.GetSnapshotAsync(pr, ct).ConfigureAwait(false);
        var tokenOwner = await provider.GetCurrentUserAsync(ct).ConfigureAwait(false);

        // A new config re-reviews a head already reviewed under the old one; otherwise it applies from the next delta.
        var previous = await store.GetPreviousReviewAsync(pr, ct).ConfigureAwait(false);
        var whole = request.Full
            || previous is null
            || (previous.ConfigHash != request.ConfigHash && string.Equals(previous.State.HeadSha, snapshot.HeadSha, StringComparison.OrdinalIgnoreCase));
        var deltaPlan = await builder.PlanAsync(snapshot, request.Source, whole ? null : previous!.State, ct).ConfigureAwait(false);
        var delta = deltaPlan.Delta;

        if (delta.Kind == DeltaKind.Unchanged)
        {
            await store.UpdateJobAsync(jobId, snapshot, delta, null, ct).ConfigureAwait(false);
            await store.SkipJobAsync(jobId, delta.Reason, CancellationToken.None).ConfigureAwait(false);
            var nothing = new ReviewPlan(delta.Reason, [], [], [], [], ReviewDecision.None, Merge: false, UnresolvedThreads: false, [delta.Reason]);
            return new ReviewOutcome(jobId, null, snapshot, delta, new FindingsReport("", []), nothing, Published: false, Skipped: true);
        }

        await using var prepared = await builder
            .PrepareAsync(jobId, snapshot, deltaPlan, tokenOwner, request.Instructions, request.Timeout, ct)
            .ConfigureAwait(false);
        var attempt = prepared.Context.AttemptFolder;
        await store.UpdateJobAsync(jobId, snapshot, delta, attempt, ct).ConfigureAwait(false);
        var report = await agent.ReviewAsync(prepared.Context, ct).ConfigureAwait(false);

        var fingerprints = new Dictionary<Finding, string>();
        foreach (var finding in report.Findings)
        {
            var snippet = await SnippetAsync(prepared, finding, ct).ConfigureAwait(false);
            fingerprints[finding] = FindingFingerprint.Compute(finding, snippet);
        }

        // Earlier findings are fixed when their lines changed and the agent didn't report them again; the rest stay open.
        // Open blockers block even when reported again: a re-report outside this delta wouldn't block by itself.
        var open = await store.GetOpenFindingsAsync(pr, ct).ConfigureAwait(false);
        var reported = fingerprints.Values.ToHashSet(StringComparer.Ordinal);
        var touched = await builder.FindTouchedAsync(open, snapshot.HeadSha, ct).ConfigureAwait(false);
        var resolved = open.Where(o => !reported.Contains(o.Fingerprint) && touched.Contains(o.Fingerprint)).Select(o => o.Fingerprint).ToList();
        var stillOpen = open.Where(o => !resolved.Contains(o.Fingerprint)).ToList();

        var plan = PolicyEngine.Decide(
            report, snapshot, request.Policy, provider.Capabilities, prepared.Scope, [.. stillOpen.Where(o => o.Blocking).Select(o => o.Finding)]);

        // Don't post what the PR already has: comments with our fingerprint marker, and findings from earlier summaries.
        var posted = (await provider.GetOwnCommentsAsync(pr, ct).ConfigureAwait(false)).Select(c => c.Fingerprint)
            .Concat(open.Select(o => o.Fingerprint))
            .ToHashSet(StringComparer.Ordinal);
        plan = WithoutPosted(plan, fingerprints, posted);
        await File.WriteAllTextAsync(Path.Combine(attempt, PlanFile), JsonSerializer.Serialize(plan, PlanJson), ct)
            .ConfigureAwait(false);

        var published = !request.Policy.DryRun;
        if (published)
        {
            // Merging is gate-merge only, which comes in M3; until then the plan never asks for it.
            var result = new ReviewResult(
                $"{plan.Summary}\n\n{Footer(delta, prepared.Scope.ReviewedHeadSha, resolved.Count, stillOpen.Count, plan.OpenBlockingFindings.Count)}",
                [.. plan.InlineFindings.Select(f => new InlineComment(f, fingerprints[f]))],
                plan.SummaryFindings,
                plan.Decision,
                prepared.Scope.ReviewedHeadSha);
            await provider.PublishReviewAsync(pr, result, ct).ConfigureAwait(false);
            await builder.KeepReviewedHeadAsync(pr, snapshot.HeadSha, CancellationToken.None).ConfigureAwait(false);
        }

        var outcome = new ReviewOutcome(jobId, attempt, snapshot, delta, report, plan, published, Resolved: resolved.Count, StillOpen: stillOpen.Count);
        await store.CompleteJobAsync(jobId, outcome, fingerprints, deltaPlan.Current, resolved, CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Moves kept findings the PR already has out of the comments. The decision doesn't change: an old blocker still blocks.</summary>
    internal static ReviewPlan WithoutPosted(ReviewPlan plan, IReadOnlyDictionary<Finding, string> fingerprints, IReadOnlySet<string> posted)
    {
        bool New(Finding f) => !posted.Contains(fingerprints[f]);
        var already = plan.InlineFindings.Concat(plan.SummaryFindings).Where(f => !New(f)).ToList();
        return already.Count == 0
            ? plan
            : plan with
            {
                InlineFindings = [.. plan.InlineFindings.Where(New)],
                SummaryFindings = [.. plan.SummaryFindings.Where(New)],
                AlreadyPosted = already,
                Reasons = [.. plan.Reasons, $"Left out {already.Count} finding(s) the PR already has from an earlier review."],
            };
    }

    private string Footer(ReviewDelta delta, string head, int resolved, int stillOpen, int openBlocking)
    {
        var scope = delta.Kind == DeltaKind.Full ? "" : $" ({delta.Reason.TrimEnd('.').ToLowerInvariant()})";
        var notes = new List<string> { $"Reviewed {Short(head)}{scope} with {agent.Id}." };
        if (resolved > 0)
        {
            notes.Add($"{resolved} earlier finding(s) fixed.");
        }

        if (stillOpen > 0)
        {
            notes.Add($"{stillOpen} earlier finding(s) still open{(openBlocking > 0 ? $", {openBlocking} blocking" : "")}.");
        }

        return $"<sub>{string.Join(" ", notes)}</sub>";
    }

    // Best effort: a failure to record the failure must not hide the review's own error.
    private async Task FailQuietlyAsync(string jobId, JobState state, string error, DateTimeOffset? retryAt)
    {
        try
        {
            await store.FailJobAsync(jobId, state, error, retryAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DbException or DbUpdateException)
        {
        }
    }

    private static async Task<string> SnippetAsync(PreparedReview prepared, Finding finding, CancellationToken ct)
    {
        // The path comes from the model and the files from the PR, so read from git rather than the worktree: git
        // returns a symlink's target as text instead of following it, and paths outside the tree simply don't exist.
        if (await prepared.ReadHeadFileAsync(finding.File, ct).ConfigureAwait(false) is not { } content)
        {
            return "";
        }

        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var start = Math.Clamp(finding.StartLine, 1, lines.Length + 1) - 1;
        var end = Math.Clamp(finding.EndLine, start, lines.Length);
        return string.Join('\n', lines[start..end]);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
