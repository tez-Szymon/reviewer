using System.Net;

namespace Reviewer.Providers.GitHub;

public sealed class GitHubApiException(HttpStatusCode status, string message)
    : Exception($"GitHub API returned {(int)status} {status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}
