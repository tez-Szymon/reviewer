using System.Net;
using Reviewer.Core.Providers;
using Reviewer.Providers.GitHub;

namespace Reviewer.Api.Tests;

/// <summary>A provider that only knows its user and the repos it can see, for the account endpoints.</summary>
internal sealed class FakeProvider : IRepositoryProvider
{
    public string User { get; set; } = "reviewer-bot";

    /// <summary>Thrown by every call, as when the token is rejected.</summary>
    public Exception? Error { get; set; }

    public List<RepoRef> Repos { get; } = [];

    /// <summary>The owner each listing was asked for.</summary>
    public List<string?> Listed { get; } = [];

    public ProviderCapabilities Capabilities { get; } = new(true, true, true, true);

    public Task<string> GetCurrentUserAsync(CancellationToken ct) => Error is { } e ? Task.FromException<string>(e) : Task.FromResult(User);

    public async IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Listed.Add(owner);
        await Task.Yield();
        if (Error is { } e)
        {
            throw e;
        }

        foreach (var repo in Repos.Where(r => owner is null || r.Owner == owner))
        {
            yield return repo;
        }
    }

    public static GitHubApiException Unauthorized() => new(HttpStatusCode.Unauthorized, "Bad credentials");

    public Task<PullRequestList> ListOpenPullRequestsAsync(RepoRef repo, string? etag, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken ct) => throw new NotSupportedException();

    public Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct) => throw new NotSupportedException();

    public Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct) => throw new NotSupportedException();

    public Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, string note, CancellationToken ct) => throw new NotSupportedException();

    public Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct) => throw new NotSupportedException();
}
