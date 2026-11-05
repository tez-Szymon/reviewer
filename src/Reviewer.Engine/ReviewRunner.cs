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
/// <param name="Dismissed">Earlier findings whose thread a person resolved on the PR; they no longer block.</param>
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
    int StillOpen = 0,
    int Dismissed = 0)
{
    /// <summary>Things that went wrong after the review was published, such as a thread that couldn't be resolved.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>What the platform accepted: a merge, or a merge once CI passes. None when the merge was refused.</summary>
    public MergeIntent Merged { get; init; }
}

/// <summary>
/// One review from start to finish: snapshot, delta, context, agent, policy, publish. The agent's findings reach the PR
/// only through the policy engine's plan. Every run is recorded in the store, including the ones that fail.
/// </summary>
/// <remarks>
/// A re-review covers only what's new, so findings from earlier reviews are tracked here. The agent checks each one
/// against the head; one it finds fixed has its thread resolved with the agent's reason. One whose thread a person
/// resolved is dismissed and isn't checked. Any other stays open, isn't posted again, and if it was blocking it keeps
/// blocking: approving needs every blocker fixed or dismissed, not only the ones in the latest delta. When the author
/// changed the code of one that's still open, its thread gets a reply saying why.
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
                Full = request.Full,
            },
            ct).ConfigureAwait(false);
        return await RunJobAsync(jobId, request, queued: false, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a queued job that this process has claimed with <see cref="ReviewStore.TryClaimJobAsync"/>.</summary>
    /// <exception cref="AgentException">The agent is unavailable, throttled, failed or returned invalid output.</exception>
    public Task<ReviewOutcome> RunQueuedAsync(string jobId, ReviewRequest request, CancellationToken ct) =>
        RunJobAsync(jobId, request, queued: true, ct);

    private async Task<ReviewOutcome> RunJobAsync(string jobId, ReviewRequest request, bool queued, CancellationToken ct)
    {
        try
        {
            if (queued)
            {
                await store.SetJobConfigAsync(jobId, request.ConfigHash, agent.Id, request.Policy.DryRun, ct).ConfigureAwait(false);
            }

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
            var nothing = new ReviewPlan(delta.Reason, [], [], [], [], ReviewDecision.None, MergeIntent.None, UnresolvedThreads: false, [delta.Reason]);
            return new ReviewOutcome(jobId, null, snapshot, delta, new FindingsReport("", []), nothing, Published: false, Skipped: true);
        }

        // Earlier findings still open on the PR go to the agent to check. One whose thread a person resolved is dismissed:
        // a person's call stands, so it isn't checked.
        var open = await store.GetOpenFindingsAsync(pr, ct).ConfigureAwait(false);
        var own = await provider.GetOwnCommentsAsync(pr, ct).ConfigureAwait(false);
        var resolvedOnPr = own.Where(c => c.IsResolved).Select(c => c.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var dismissed = open.Where(o => resolvedOnPr.Contains(o.Fingerprint)).Select(o => o.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var toCheck = open.Where(o => !dismissed.Contains(o.Fingerprint)).ToList();

        await using var prepared = await builder
            .PrepareAsync(jobId, snapshot, deltaPlan, tokenOwner, request.Instructions, request.Timeout, ct, toCheck)
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

        // The agent's verdict decides whether an earlier finding is fixed, wherever the fix is. Without one (an older
        // agent, or one it skipped), it's fixed when its lines changed and it wasn't reported again. A re-report always
        // keeps it open. Open blockers block even when reported again: a re-report outside this delta wouldn't block by itself.
        var reported = fingerprints.Values.ToHashSet(StringComparer.Ordinal);
        var verdicts = Verdicts(report, toCheck);
        var touched = await builder.FindTouchedAsync(toCheck, snapshot.HeadSha, ct).ConfigureAwait(false);
        var fixedFingerprints = toCheck
            .Where(o => !reported.Contains(o.Fingerprint) && verdicts.GetValueOrDefault(o.Fingerprint)?.Status switch
            {
                EarlierFindingStatus.Fixed => true,
                EarlierFindingStatus.Open => false,
                _ => touched.Contains(o.Fingerprint),
            })
            .Select(o => o.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var stillOpen = toCheck.Where(o => !fixedFingerprints.Contains(o.Fingerprint)).ToList();

        // A person's call stands: a dismissed finding reported again doesn't block. It's kept in the report, so history
        // shows it, as dropped. Checked against the PR, not `dismissed`: once a review records the dismissal the finding
        // is no longer open, but its thread stays resolved.
        var considered = report with { Findings = [.. report.Findings.Where(f => !resolvedOnPr.Contains(fingerprints[f]))] };

        // The agent took minutes: CI, mergeability and the head may have moved since the snapshot, so merge on a fresh one.
        var current = request.Policy.Mode == ReviewMode.GateMerge
            ? await provider.GetSnapshotAsync(pr, ct).ConfigureAwait(false)
            : snapshot;
        var plan = PolicyEngine.Decide(
            considered, current, request.Policy, provider.Capabilities, prepared.Scope, [.. stillOpen.Where(o => o.Blocking).Select(o => o.Finding)]);
        if (considered.Findings.Count < report.Findings.Count)
        {
            plan = plan with { Reasons = [.. plan.Reasons, $"Ignored {report.Findings.Count - considered.Findings.Count} finding(s) whose thread a person resolved."] };
        }

        // Don't post what the PR already has: comments with our fingerprint marker, and findings from earlier summaries.
        var posted = own.Select(c => c.Fingerprint).Concat(open.Select(o => o.Fingerprint)).ToHashSet(StringComparer.Ordinal);
        plan = WithoutPosted(plan, fingerprints, posted);
        if (provider.Capabilities.SupportsThreadResolve)
        {
            plan = plan with { CommentsToResolve = [.. own.Where(c => !c.IsResolved && fixedFingerprints.Contains(c.Fingerprint))] };
        }

        // The author changed the code of a finding the agent found still there: say why on its thread, so the push
        // doesn't go unanswered. Only when the push since the last review changed its lines, so a later push elsewhere
        // in the file adds no noise.
        var touchedNow = await builder.FindTouchedSinceAsync(stillOpen, previous?.State.HeadSha, snapshot.HeadSha, ct).ConfigureAwait(false);
        var answered = stillOpen
            .Where(o => verdicts.GetValueOrDefault(o.Fingerprint) is { Status: EarlierFindingStatus.Open } verdict
                && verdict.Reason.Trim().Length > 0
                && touchedNow.Contains(o.Fingerprint))
            .Select(o => o.Fingerprint).ToHashSet(StringComparer.Ordinal);
        plan = plan with
        {
            CommentsToReply = [.. own
                .Where(c => !c.IsResolved && answered.Contains(c.Fingerprint))
                .DistinctBy(c => c.Fingerprint)
                .Select(c => new ThreadNote(c, $"Still open in {Short(prepared.Scope.ReviewedHeadSha)}: {verdicts[c.Fingerprint].Reason.Trim()}"))],
        };

        await File.WriteAllTextAsync(Path.Combine(attempt, PlanFile), JsonSerializer.Serialize(plan, PlanJson), ct)
            .ConfigureAwait(false);

        var published = !request.Policy.DryRun;
        var warnings = new List<string>();
        var merged = MergeIntent.None;
        if (published)
        {
            var head = prepared.Scope.ReviewedHeadSha;
            var reReview = delta.Kind != DeltaKind.Full;
            var footer = Footer(delta, head, fixedFingerprints.Count, dismissed.Count, stillOpen.Count, plan.OpenBlockingFindings.Count);
            var details = string.Join("\n\n", new[]
            {
                CommentFormatter.Details(considered, reReview),
                CommentFormatter.StillOpen([.. stillOpen.Select(o => (o.Finding, verdicts.GetValueOrDefault(o.Fingerprint)?.Reason))]),
            }.Where(d => d.Length > 0));
            var result = new ReviewResult(
                CommentFormatter.Headline(considered with { Summary = plan.Summary }, [.. plan.InlineFindings, .. plan.SummaryFindings], reReview),
                [.. plan.InlineFindings.Select(f => new InlineComment(f, fingerprints[f]))],
                plan.SummaryFindings,
                plan.Decision,
                head)
            {
                Details = details.Length > 0 ? $"{details}\n\n{footer}" : footer,
                WithdrawApproval = plan.WithdrawApproval,
            };
            await provider.PublishReviewAsync(pr, result, ct).ConfigureAwait(false);
            await builder.KeepReviewedHeadAsync(pr, snapshot.HeadSha, CancellationToken.None).ConfigureAwait(false);

            // After the review, so a failure here can't lose it. The finding is fixed either way; only the thread stays open.
            foreach (var comment in plan.CommentsToResolve)
            {
                try
                {
                    var why = verdicts.GetValueOrDefault(comment.Fingerprint) is { Status: EarlierFindingStatus.Fixed, Reason: var reason }
                        && reason.Trim().Length > 0
                        ? reason.Trim()
                        : "this code changed and the review didn't find the issue again.";
                    await provider.ResolveCommentAsync(pr, comment, $"Fixed in {Short(head)}: {why}", ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    warnings.Add($"Couldn't resolve the thread at {comment.File}:{comment.Line}: {e.Message}");
                }
            }

            foreach (var reply in plan.CommentsToReply)
            {
                try
                {
                    await provider.ReplyToCommentAsync(pr, reply.Comment, reply.Note, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    warnings.Add($"Couldn't reply on the thread at {reply.Comment.File}:{reply.Comment.Line}: {e.Message}");
                }
            }

            // Last, once the approval is on the PR. The platform refuses any head but the reviewed one, and enforces its
            // own rules, such as required approvals; a refusal leaves the review standing.
            if (plan.Merge != MergeIntent.None)
            {
                try
                {
                    await provider.MergeAsync(
                        pr, new MergeOptions(request.Policy.MergeMethod, head, WhenGreen: plan.Merge == MergeIntent.MergeWhenGreen), ct)
                        .ConfigureAwait(false);
                    merged = plan.Merge;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    warnings.Add(plan.Merge == MergeIntent.MergeWhenGreen
                        ? $"Couldn't set the PR to merge when CI passes: {e.Message}"
                        : $"Couldn't merge: {e.Message}");
                }
            }
        }

        var outcome = new ReviewOutcome(
            jobId, attempt, snapshot, delta, report, plan, published,
            Resolved: fixedFingerprints.Count, StillOpen: stillOpen.Count, Dismissed: dismissed.Count)
        {
            Warnings = warnings,
            Merged = merged,
        };
        await store.CompleteJobAsync(jobId, outcome, fingerprints, deltaPlan.Current, [.. fixedFingerprints, .. dismissed], CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>The agent's verdict on each earlier finding it was asked about, by fingerprint. Unknown ids are ignored.</summary>
    internal static Dictionary<string, EarlierFindingCheck> Verdicts(FindingsReport report, IReadOnlyList<OpenFinding> asked)
    {
        var ids = asked.Select(o => o.Fingerprint).ToHashSet(StringComparer.Ordinal);
        return (report.EarlierFindings ?? [])
            .Where(v => ids.Contains(v.Id))
            .DistinctBy(v => v.Id, StringComparer.Ordinal)
            .ToDictionary(v => v.Id, StringComparer.Ordinal);
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

    private string Footer(ReviewDelta delta, string head, int fixedCount, int dismissed, int stillOpen, int openBlocking)
    {
        var scope = delta.Kind == DeltaKind.Full ? "" : $" ({delta.Reason.TrimEnd('.').ToLowerInvariant()})";
        var notes = new List<string> { $"Reviewed {Short(head)}{scope} with {agent.Id}." };
        if (fixedCount > 0)
        {
            notes.Add($"{fixedCount} earlier finding(s) fixed.");
        }

        if (dismissed > 0)
        {
            notes.Add($"{dismissed} dismissed on the PR.");
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
