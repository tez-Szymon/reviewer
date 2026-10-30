using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Core.Policy;

public enum ReviewMode
{
    Comment,
    Approve,
    Gate,
    GateMerge,
}

/// <summary>What to do instead of "request changes" on a provider that lacks it (e.g. GitLab).</summary>
public enum RequestChangesFallback
{
    /// <summary>Post blocking findings as unresolved threads, which the platform can require resolving before merge.</summary>
    UnresolvedThreads,

    /// <summary>Post comments only.</summary>
    Comment,
}

/// <summary>A finding is blocking when it matches every condition the rule sets.</summary>
public sealed record BlockingRule(
    Severity MinSeverity,
    IReadOnlyList<Category>? Categories = null,
    double? MinConfidence = null)
{
    public bool Matches(Finding finding) =>
        finding.Severity >= MinSeverity
        && (Categories is null || Categories.Contains(finding.Category))
        && (MinConfidence is null || finding.Confidence >= MinConfidence);
}

public sealed record ReviewPolicy(
    ReviewMode Mode,
    bool DryRun,
    double MinConfidence,
    IReadOnlyList<BlockingRule> BlockingRules,
    IReadOnlyList<string> IgnorePaths,
    int MaxInlineComments,
    RequestChangesFallback RequestChangesFallback = RequestChangesFallback.UnresolvedThreads);

/// <summary>What the publisher will do. Produced only by the policy engine, never by the model.</summary>
/// <param name="InlineFindings">Findings posted as inline comments, most important first.</param>
/// <param name="SummaryFindings">Kept findings listed in the summary only: outside the delta or over the inline limit.</param>
/// <param name="BlockingFindingIds">Ids of kept findings that matched a blocking rule.</param>
/// <param name="UnresolvedThreads">Post blocking inline findings as unresolved threads (request-changes fallback).</param>
/// <param name="Reasons">Why the engine decided as it did, for history and dry runs.</param>
public sealed record ReviewPlan(
    string Summary,
    IReadOnlyList<Finding> InlineFindings,
    IReadOnlyList<Finding> SummaryFindings,
    IReadOnlyList<string> BlockingFindingIds,
    IReadOnlyList<PublishedComment> CommentsToResolve,
    ReviewDecision Decision,
    bool Merge,
    bool UnresolvedThreads,
    IReadOnlyList<string> Reasons);
