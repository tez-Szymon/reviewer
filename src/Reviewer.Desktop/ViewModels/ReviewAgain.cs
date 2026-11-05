using System.Net;
using Reviewer.Api;

namespace Reviewer.Desktop.ViewModels;

/// <summary>Queues a review of the whole PR on request, even when nothing was pushed since the last one.</summary>
internal static class ReviewAgain
{
    /// <exception cref="EngineApiException">No account serves the PR's host, or the engine refused the request.</exception>
    public static async Task<QueueResult> QueueAsync(IEngineClient client, string host, string repo, int number)
    {
        var accounts = await client.GetAccountsAsync(CancellationToken.None);
        var url = JobDetailViewModel.Link(host, repo, number, accounts)
            ?? throw new EngineApiException(HttpStatusCode.BadRequest, $"No account is set up for {host}, so {repo} can't be reviewed.");

        // Full: a review that only covered what's new would be skipped when the head hasn't moved.
        return await client.QueueReviewAsync(new QueueRequest(url.AbsoluteUri, Full: true), CancellationToken.None);
    }
}
