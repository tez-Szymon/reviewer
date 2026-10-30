namespace Reviewer.Core.Providers;

/// <summary>The PR/MR layer of one hosting platform. Git operations live in Reviewer.Git.</summary>
public interface IRepositoryProvider
{
    ProviderCapabilities Capabilities { get; }

    /// <summary>Login of the account whose token this provider uses.</summary>
    Task<string> GetCurrentUserAsync(CancellationToken ct);

    IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct);
    IAsyncEnumerable<PullRequestRef> ListCandidatesAsync(RepoRef repo, TriggerFilter filter, CancellationToken ct);
    Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct);
    Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct);

    /// <summary>Posts the summary, inline comments and decision as one review bound to <see cref="ReviewResult.HeadSha"/>.</summary>
    Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct);
    /// <summary>Replies to <paramref name="comment"/> with <paramref name="note"/> and resolves its thread.</summary>
    Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, string note, CancellationToken ct);
    Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct);
}
