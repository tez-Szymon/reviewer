using Reviewer.Core.Providers;

namespace Reviewer.Engine.Tests;

/// <summary>A provider that records what's published and reports it back as the token owner's comments.</summary>
internal sealed class FakeProvider(PullRequestSnapshot snapshot) : IRepositoryProvider
{
    public PullRequestSnapshot Snapshot { get; set; } = snapshot;

    public List<ReviewResult> Published { get; } = [];

    public ProviderCapabilities Capabilities { get; } = new(true, true, true, false);

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

    public IAsyncEnumerable<PullRequestRef> ListCandidatesAsync(RepoRef repo, TriggerFilter filter, CancellationToken ct) =>
        throw new NotSupportedException();


    public Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct) => throw new NotSupportedException();
}
