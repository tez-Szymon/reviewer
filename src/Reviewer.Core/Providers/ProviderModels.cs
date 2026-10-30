using Reviewer.Core.Findings;

namespace Reviewer.Core.Providers;

public sealed record RepoRef(Uri Host, string Owner, string Name)
{
    public override string ToString() => $"{Owner}/{Name}";
}

public sealed record PullRequestRef(RepoRef Repo, int Number);

public enum CiState
{
    Unknown,
    Pending,
    Success,
    Failure,
}

/// <summary>Provider-neutral view of a PR; each adapter maps its own model into it.</summary>
public sealed record PullRequestSnapshot(
    PullRequestRef Ref,
    string HeadSha,
    string BaseSha,
    string BaseBranch,
    string Author,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<string> RequestedReviewers,
    IReadOnlyList<string> Labels,
    bool IsDraft,
    CiState Ci,
    bool IsMergeable);

public sealed record ProviderCapabilities(
    bool SupportsRequestChanges,
    bool SupportsThreadResolve,
    bool SupportsSuggestions,
    bool SupportsMergeWhenGreen);

public sealed record TriggerFilter(
    bool AssignedToMe,
    bool ReviewRequested,
    bool SkipDrafts,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> TargetBranches);

/// <summary>A comment this tool posted earlier, linked to the finding that produced it.</summary>
public sealed record PublishedComment(string ProviderId, string Fingerprint, string? File, int? Line);

/// <summary>A finding to post inline, with the fingerprint the engine computed for dedup across runs.</summary>
public sealed record InlineComment(Finding Finding, string Fingerprint);

/// <param name="HeadSha">The commit that was reviewed. The decision applies to this commit only.</param>
public sealed record ReviewResult(string Summary, IReadOnlyList<InlineComment> Comments, ReviewDecision Decision, string HeadSha);

public enum ReviewDecision
{
    None,
    Approve,
    RequestChanges,
}

public enum MergeMethod
{
    Merge,
    Squash,
    Rebase,
}

public sealed record MergeOptions(MergeMethod Method, string ExpectedHeadSha);
