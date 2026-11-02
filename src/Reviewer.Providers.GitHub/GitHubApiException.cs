using System.Net;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub;

public sealed class GitHubApiException(HttpStatusCode status, string message)
    : ProviderApiException(status, $"GitHub API returned {(int)status} {status}: {message}");
