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
public sealed record ReviewRequest(
    PullRequestRef PullRequest,
    ReviewSource Source,
    ReviewPolicy Policy,
    InstructionSettings Instructions,
    TimeSpan Timeout,
    string ConfigHash);

/// <param name="AttemptFolder">Holds the agent's inputs, its output and <c>plan.json</c>.</param>
/// <param name="Published">False for a dry run.</param>
public sealed record ReviewOutcome(
    string JobId,
    string AttemptFolder,
    PullRequestSnapshot Snapshot,
    FindingsReport Report,
    ReviewPlan Plan,
    bool Published);

/// <summary>
/// One review from start to finish: snapshot, context, agent, policy, publish. The agent's findings reach the PR only
/// through the policy engine's plan. Every run is recorded in the store, including the ones that fail.
/// </summary>
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
            var (outcome, fingerprints) = await ReviewAsync(jobId, request, ct).ConfigureAwait(false);
            await store.CompleteJobAsync(jobId, outcome, fingerprints, CancellationToken.None).ConfigureAwait(false);
            return outcome;
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

    private async Task<(ReviewOutcome Outcome, Dictionary<Finding, string> Fingerprints)> ReviewAsync(
        string jobId, ReviewRequest request, CancellationToken ct)
    {
        var snapshot = await provider.GetSnapshotAsync(request.PullRequest, ct).ConfigureAwait(false);
        var tokenOwner = await provider.GetCurrentUserAsync(ct).ConfigureAwait(false);

        await using var prepared = await builder
            .PrepareAsync(jobId, snapshot, request.Source, tokenOwner, request.Instructions, request.Timeout, ct)
            .ConfigureAwait(false);
        var attempt = prepared.Context.AttemptFolder;
        await store.UpdateJobAsync(jobId, snapshot, attempt, ct).ConfigureAwait(false);
        var report = await agent.ReviewAsync(prepared.Context, ct).ConfigureAwait(false);

        var plan = PolicyEngine.Decide(report, snapshot, request.Policy, provider.Capabilities, prepared.Scope);
        await File.WriteAllTextAsync(Path.Combine(attempt, PlanFile), JsonSerializer.Serialize(plan, PlanJson), ct)
            .ConfigureAwait(false);

        // Summary findings get fingerprints too: history keeps them, and dedup will compare against them.
        var fingerprints = new Dictionary<Finding, string>();
        foreach (var finding in plan.InlineFindings.Concat(plan.SummaryFindings))
        {
            var snippet = await SnippetAsync(prepared, finding, ct).ConfigureAwait(false);
            fingerprints[finding] = FindingFingerprint.Compute(finding, snippet);
        }

        if (request.Policy.DryRun)
        {
            return (new ReviewOutcome(jobId, attempt, snapshot, report, plan, Published: false), fingerprints);
        }

        // Merging is gate-merge only, which comes in M3; until then the plan never asks for it.
        var result = new ReviewResult(
            $"{plan.Summary}\n\n<sub>Reviewed {Short(prepared.Scope.ReviewedHeadSha)} with {agent.Id}.</sub>",
            [.. plan.InlineFindings.Select(f => new InlineComment(f, fingerprints[f]))],
            plan.SummaryFindings,
            plan.Decision,
            prepared.Scope.ReviewedHeadSha);
        await provider.PublishReviewAsync(request.PullRequest, result, ct).ConfigureAwait(false);
        return (new ReviewOutcome(jobId, attempt, snapshot, report, plan, Published: true), fingerprints);
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
