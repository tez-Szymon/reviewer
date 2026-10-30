using System.Text.Json;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;

namespace Reviewer.Engine;

public sealed record ReviewRequest(
    PullRequestRef PullRequest,
    ReviewSource Source,
    ReviewPolicy Policy,
    InstructionSettings Instructions,
    TimeSpan Timeout);

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
/// through the policy engine's plan.
/// </summary>
public sealed class ReviewRunner(IRepositoryProvider provider, ReviewContextBuilder builder, IReviewAgent agent)
{
    public const string PlanFile = "plan.json";

    private static readonly JsonSerializerOptions PlanJson = new(FindingsJson.Options) { WriteIndented = true };

    /// <exception cref="AgentException">The agent is unavailable, throttled, failed or returned invalid output.</exception>
    public async Task<ReviewOutcome> RunAsync(ReviewRequest request, CancellationToken ct)
    {
        var snapshot = await provider.GetSnapshotAsync(request.PullRequest, ct).ConfigureAwait(false);
        var tokenOwner = await provider.GetCurrentUserAsync(ct).ConfigureAwait(false);

        await using var prepared = await builder
            .PrepareAsync(snapshot, request.Source, tokenOwner, request.Instructions, request.Timeout, ct)
            .ConfigureAwait(false);
        var report = await agent.ReviewAsync(prepared.Context, ct).ConfigureAwait(false);

        var plan = PolicyEngine.Decide(report, snapshot, request.Policy, provider.Capabilities, prepared.Scope);
        var attempt = prepared.Context.AttemptFolder;
        await File.WriteAllTextAsync(Path.Combine(attempt, PlanFile), JsonSerializer.Serialize(plan, PlanJson), ct)
            .ConfigureAwait(false);

        if (request.Policy.DryRun)
        {
            return new ReviewOutcome(prepared.JobId, attempt, snapshot, report, plan, Published: false);
        }

        // Fingerprints hash the code a finding points at, so they're taken while the worktree still exists.
        var comments = new List<InlineComment>(plan.InlineFindings.Count);
        foreach (var finding in plan.InlineFindings)
        {
            var snippet = await SnippetAsync(prepared.Context.WorktreePath, finding, ct).ConfigureAwait(false);
            comments.Add(new InlineComment(finding, FindingFingerprint.Compute(finding, snippet)));
        }

        // Merging is gate-merge only, which comes in M3; until then the plan never asks for it.
        var result = new ReviewResult(
            $"{plan.Summary}\n\n<sub>Reviewed {Short(prepared.Scope.ReviewedHeadSha)} with {agent.Id}.</sub>",
            comments,
            plan.SummaryFindings,
            plan.Decision,
            prepared.Scope.ReviewedHeadSha);
        await provider.PublishReviewAsync(request.PullRequest, result, ct).ConfigureAwait(false);
        return new ReviewOutcome(prepared.JobId, attempt, snapshot, report, plan, Published: true);
    }

    private static async Task<string> SnippetAsync(string worktree, Finding finding, CancellationToken ct)
    {
        // The path comes from the model: read only inside the worktree.
        var root = Path.GetFullPath(worktree) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, finding.File));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
        {
            return "";
        }

        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var start = Math.Clamp(finding.StartLine, 1, lines.Length + 1) - 1;
        var end = Math.Clamp(finding.EndLine, start, lines.Length);
        return string.Join('\n', lines[start..end]);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
