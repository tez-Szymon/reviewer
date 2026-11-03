using System.Net;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitLab;

public sealed class GitLabApiException(HttpStatusCode status, string message)
    : ProviderApiException(status, $"GitLab API returned {(int)status} {status}: {message}");
