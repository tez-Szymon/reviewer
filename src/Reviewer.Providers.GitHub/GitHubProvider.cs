using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub;

/// <summary>GitHub REST/GraphQL adapter. M1 scope: snapshot, publish review, own comments.</summary>
public sealed class GitHubProvider(HttpClient http) : IRepositoryProvider
{
    private readonly HttpClient _http = http;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsRequestChanges: true,
        SupportsThreadResolve: true,
        SupportsSuggestions: true,
        SupportsMergeWhenGreen: false);

    public IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<PullRequestRef> ListCandidatesAsync(RepoRef repo, TriggerFilter filter, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task SetDecisionAsync(PullRequestRef pr, ReviewDecision decision, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct) =>
        throw new NotImplementedException();
}
