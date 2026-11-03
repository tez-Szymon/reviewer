using System.Globalization;
using Microsoft.Extensions.FileSystemGlobbing;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Core.Policy;

/// <summary>
/// The only place that chooses approve, request changes or merge. Pure: no I/O, no LLM.
/// </summary>
public static class PolicyEngine
{
    /// <param name="openFindings">
    /// Blocking findings from earlier reviews of this PR that are still open. They pass through the current filter and
    /// blocking rules, and block like new ones wherever they are, since they were in an earlier delta.
    /// </param>
    public static ReviewPlan Decide(
        FindingsReport report,
        PullRequestSnapshot snapshot,
        ReviewPolicy policy,
        ProviderCapabilities capabilities,
        ReviewScope scope,
        IReadOnlyList<Finding>? openFindings = null)
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
            reasons.Add(string.Create(CultureInfo.InvariantCulture,
                $"Dropped {report.Findings.Count - kept.Count} finding(s) below confidence {policy.MinConfidence} or in ignored paths."));
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

        // Earlier findings were in an earlier delta, so they aren't held to this one; filter and rules still apply.
        var openBlocking = (openFindings ?? [])
            .Where(f => f.Confidence >= policy.MinConfidence && !ignored.Match(f.File).HasMatches && policy.BlockingRules.Any(r => r.Matches(f)))
            .ToList();
        reasons.Add(blockingIds.Count == 0 ? "No blocking findings." : $"{blockingIds.Count} blocking finding(s).");
        if (openBlocking.Count > 0)
        {
            reasons.Add($"{openBlocking.Count} blocking finding(s) from earlier reviews still open.");
        }

        var blocked = blockingIds.Count + openBlocking.Count > 0;

        var inline = classified.Take(Math.Max(policy.MaxInlineComments, 0)).Select(c => c.Finding).ToList();
        var summaryOnly = classified.Skip(inline.Count).Select(c => c.Finding).Concat(outsideDelta).ToList();

        // 3. Decide by mode.
        var decision = (policy.Mode, blocked) switch
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
        var withdrawApproval = false;
        if (decision == ReviewDecision.RequestChanges && !capabilities.SupportsRequestChanges)
        {
            decision = ReviewDecision.None;
            withdrawApproval = true;
            unresolvedThreads = policy.RequestChangesFallback == RequestChangesFallback.UnresolvedThreads;
            reasons.Add($"Provider can't request changes: falling back to {policy.RequestChangesFallback}.");
        }

        // 4. Merge guards.
        var merge = MergeIntent.None;
        if (policy.Mode == ReviewMode.GateMerge && decision == ReviewDecision.Approve)
        {
            (merge, var reason) = DecideMerge(snapshot, policy, capabilities, scope);
            reasons.Add(reason);
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
            reasons)
        {
            OpenBlockingFindings = openBlocking,
            WithdrawApproval = withdrawApproval,
        };
    }

    /// <summary>
    /// Whether an approved PR can be merged now, once CI passes, or not at all. Required approvals are enforced by the
    /// platform when merging, and the platform also refuses a head other than the reviewed one.
    /// </summary>
    public static (MergeIntent Intent, string Reason) DecideMerge(
        PullRequestSnapshot snapshot, ReviewPolicy policy, ProviderCapabilities capabilities, ReviewScope scope)
    {
        var failed = new List<string>();
        if (snapshot.IsDraft)
        {
            failed.Add("PR is a draft");
        }

        if (!string.Equals(snapshot.HeadSha, scope.ReviewedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            failed.Add("head moved since review");
        }

        if (!snapshot.IsMergeable)
        {
            failed.Add("PR is not mergeable");
        }

        var waitForCi = false;
        if (policy.RequireGreenCi && snapshot.Ci != CiState.Success)
        {
            if (snapshot.Ci == CiState.Pending && capabilities.SupportsMergeWhenGreen)
            {
                waitForCi = true;
            }
            else
            {
                failed.Add(snapshot.Ci == CiState.Unknown ? "no CI ran" : $"CI is {snapshot.Ci}");
            }
        }

        return failed.Count > 0 ? (MergeIntent.None, $"Not merging: {string.Join("; ", failed)}.")
            : waitForCi ? (MergeIntent.MergeWhenGreen, "Merge guards passed but CI is running: merge when it passes.")
            : (MergeIntent.Merge, "Merge guards passed.");
    }
}
