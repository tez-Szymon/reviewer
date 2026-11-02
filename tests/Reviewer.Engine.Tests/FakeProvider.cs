using Reviewer.Core.Providers;

namespace Reviewer.Engine.Tests;

/// <summary>A provider that records what's published and reports it back as the token owner's comments.</summary>
internal sealed class FakeProvider(PullRequestSnapshot snapshot) : IRepositoryProvider
{
    public PullRequestSnapshot Snapshot { get; set; } = snapshot;

    public List<ReviewResult> Published { get; } = [];

    public ProviderCapabilities Capabilities { get; set; } = new(true, true, true, false);

    public Task<string> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult("bob");

    public Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct) => Task.FromResult(Snapshot);

    public Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct)
    {
        Published.Add(result);
        return Task.CompletedTask;
    }

    /// <summary>Comment ids whose threads are resolved, by this tool or as if by a person.</summary>
    public HashSet<string> ResolvedThreads { get; } = [];

    public List<(PublishedComment Comment, string Note)> Resolutions { get; } = [];

    /// <summary>Thrown by <see cref="ResolveCommentAsync"/>, to test that a failure doesn't fail the review.</summary>
    public Exception? ResolveError { get; set; }

    public Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PublishedComment>>(
            [.. Published.SelectMany(r => r.Comments).Select((c, i) =>
                new PublishedComment($"c{i}", c.Fingerprint, c.Finding.File, c.Finding.StartLine, ResolvedThreads.Contains($"c{i}")))]);

    public Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, string note, CancellationToken ct)
    {
        if (ResolveError is { } error)
        {
            throw error;
        }

        Resolutions.Add((comment, note));
        ResolvedThreads.Add(comment.ProviderId);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>The open PRs <see cref="ListOpenPullRequestsAsync"/> lists, under <see cref="ListETag"/>.</summary>
    public List<PullRequestSummary> Open { get; } = [];

    public string ListETag { get; set; } = "\"1\"";

    /// <summary>The ETag each list request was sent with.</summary>
    public List<string?> ListRequests { get; } = [];

    /// <summary>Thrown by <see cref="ListOpenPullRequestsAsync"/>, to test a failed poll.</summary>
    public Exception? ListError { get; set; }

    public Dictionary<int, IReadOnlyList<ChangedFile>> ChangedFiles { get; } = [];

    public Task<PullRequestList> ListOpenPullRequestsAsync(RepoRef repo, string? etag, CancellationToken ct)
    {
        ListRequests.Add(etag);
        if (ListError is { } error)
        {
            throw error;
        }

        return Task.FromResult(etag == ListETag ? PullRequestList.Unchanged(etag) : new PullRequestList(false, ListETag, [.. Open]));
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken ct) =>
        Task.FromResult(ChangedFiles.GetValueOrDefault(pr.Number) ?? [new ChangedFile("src/a.cs", 10, 2)]);


    public Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct) => throw new NotSupportedException();
}
