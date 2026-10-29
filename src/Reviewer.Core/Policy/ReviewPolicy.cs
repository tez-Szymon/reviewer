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

/// <summary>A finding is blocking when it matches every condition the rule sets.</summary>
public sealed record BlockingRule(
    Severity MinSeverity,
    IReadOnlyList<Category>? Categories = null,
    double? MinConfidence = null);

public sealed record ReviewPolicy(
    ReviewMode Mode,
    bool DryRun,
    double MinConfidence,
    IReadOnlyList<BlockingRule> BlockingRules,
    IReadOnlyList<string> IgnorePaths,
    int MaxInlineComments);

/// <summary>What the publisher will do. Produced only by the policy engine, never by the model.</summary>
public sealed record ReviewPlan(
    string Summary,
    IReadOnlyList<Finding> CommentsToPost,
    IReadOnlyList<PublishedComment> CommentsToResolve,
    ReviewDecision Decision,
    bool Merge);
