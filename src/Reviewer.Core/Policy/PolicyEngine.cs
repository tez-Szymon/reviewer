using Microsoft.Extensions.FileSystemGlobbing;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Core.Policy;

/// <summary>
/// The only place that chooses approve, request changes or merge. Pure: no I/O, no LLM.
/// </summary>
public static class PolicyEngine
{
    public static ReviewPlan Decide(
        FindingsReport report,
        PullRequestSnapshot snapshot,
        ReviewPolicy policy,
        ProviderCapabilities capabilities,
        ReviewScope scope)
    {
        var reasons = new List<string>();

        // 1. Filter.
        var ignored = new Matcher(StringComparison.Ordinal);
        ignored.AddIncludePatterns(policy.IgnorePaths);
        var kept = report.Findings
            .Where(f => f.Confidence >= policy.MinConfidence && !ignored.Match(f.File).HasMatches)
            .ToList();
        if (kept.Count < report.Findings.Count)
        {
            reasons.Add($"Dropped {report.Findings.Count - kept.Count} finding(s) below confidence {policy.MinConfidence} or in ignored paths.");
        }

        // Findings outside the delta can't block or be anchored inline; they go to the summary.
        var outsideDelta = kept.Where(f => !scope.Contains(f.File, f.StartLine, f.EndLine)).ToList();
        if (outsideDelta.Count > 0)
        {
            reasons.Add($"Moved {outsideDelta.Count} finding(s) outside the delta to the summary.");
        }

        // 2. Classify.
        var classified = kept
            .Where(f => scope.Contains(f.File, f.StartLine, f.EndLine))
            .Select(f => (Finding: f, Blocking: policy.BlockingRules.Any(r => r.Matches(f))))
            .OrderByDescending(c => c.Blocking)
            .ThenByDescending(c => c.Finding.Severity)
            .ThenByDescending(c => c.Finding.Confidence)
            .ToList();
        var blockingIds = classified.Where(c => c.Blocking).Select(c => c.Finding.Id).ToList();
        reasons.Add(blockingIds.Count == 0 ? "No blocking findings." : $"{blockingIds.Count} blocking finding(s).");

        var inline = classified.Take(Math.Max(policy.MaxInlineComments, 0)).Select(c => c.Finding).ToList();
        var summaryOnly = classified.Skip(inline.Count).Select(c => c.Finding).Concat(outsideDelta).ToList();

        // 3. Decide by mode.
        var decision = (policy.Mode, blockingIds.Count > 0) switch
        {
            (ReviewMode.Comment, _) => ReviewDecision.None,
            (ReviewMode.Approve, false) => ReviewDecision.Approve,
            (ReviewMode.Approve, true) => ReviewDecision.None,
            (ReviewMode.Gate or ReviewMode.GateMerge, false) => ReviewDecision.Approve,
            (ReviewMode.Gate or ReviewMode.GateMerge, true) => ReviewDecision.RequestChanges,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy.Mode, "Unknown review mode."),
        };
        reasons.Add($"Mode {policy.Mode}: {decision}.");

        // 5. Self-PR rule: the platform won't let the token owner approve or block their own PR.
        if (string.Equals(snapshot.Author, scope.TokenOwner, StringComparison.OrdinalIgnoreCase) && decision != ReviewDecision.None)
        {
            decision = ReviewDecision.None;
            reasons.Add("PR is authored by the token owner: comments only.");
        }

        // 6. Capability check.
        var unresolvedThreads = false;
        if (decision == ReviewDecision.RequestChanges && !capabilities.SupportsRequestChanges)
        {
            decision = ReviewDecision.None;
            unresolvedThreads = policy.RequestChangesFallback == RequestChangesFallback.UnresolvedThreads;
            reasons.Add($"Provider can't request changes: falling back to {policy.RequestChangesFallback}.");
        }

        // 4. Merge guards. Required approvals are enforced by the platform when merging.
        var merge = false;
        if (policy.Mode == ReviewMode.GateMerge && decision == ReviewDecision.Approve)
        {
            var failed = MergeGuardFailures(snapshot, scope).ToList();
            merge = failed.Count == 0;
            reasons.Add(merge ? "Merge guards passed." : $"Not merging: {string.Join("; ", failed)}.");
        }

        return new ReviewPlan(
            report.Summary,
            inline,
            summaryOnly,
            blockingIds,
            [],
            decision,
            merge,
            unresolvedThreads,
            reasons);
    }

    private static IEnumerable<string> MergeGuardFailures(PullRequestSnapshot snapshot, ReviewScope scope)
    {
        if (snapshot.Ci != CiState.Success)
        {
            yield return $"CI is {snapshot.Ci}";
        }

        if (!snapshot.IsMergeable)
        {
            yield return "PR is not mergeable";
        }

        if (snapshot.IsDraft)
        {
            yield return "PR is a draft";
        }

        if (!string.Equals(snapshot.HeadSha, scope.ReviewedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            yield return "head moved since review";
        }
    }
}
