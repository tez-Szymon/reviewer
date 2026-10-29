using Reviewer.Core.Policy;

namespace Reviewer.Core.Providers;

/// <summary>The PR/MR layer of one hosting platform. Git operations live in Reviewer.Git.</summary>
public interface IRepositoryProvider
{
    ProviderCapabilities Capabilities { get; }
    IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct);
    IAsyncEnumerable<PullRequestRef> ListCandidatesAsync(RepoRef repo, TriggerFilter filter, CancellationToken ct);
    Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct);
    Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct);
    Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct);
    Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, CancellationToken ct);
    Task SetDecisionAsync(PullRequestRef pr, ReviewDecision decision, CancellationToken ct);
    Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct);
}
