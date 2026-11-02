using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Providers.GitHub;
using Reviewer.Providers.GitLab;

namespace Reviewer.Api;

/// <summary>Reads a PR or MR URL by the configured accounts: GitHub's on github.com, GitLab's on each GitLab account's host.</summary>
public static class PullRequestUrls
{
    public const string Expected =
        "Expected a pull request URL, https://github.com/<owner>/<repo>/pull/<number>, or a merge request URL on a configured "
        + "GitLab account's host, <host>/<group>/<project>/-/merge_requests/<iid>.";

    public static bool TryParse(Uri url, ReviewerConfig config, out PullRequestRef pr)
    {
        foreach (var account in config.Accounts)
        {
            var parsed = account.Provider == AccountConfig.GitLab
                ? GitLabUrls.TryParseMergeRequest(url, account.Host, out pr)
                : GitHubUrls.TryParsePullRequest(url, out pr) && ReviewerConfig.SameHost(pr.Repo.Host, account.Host);
            if (parsed)
            {
                return true;
            }
        }

        // Without a GitHub account configured, a github.com URL still parses, so the error names the missing account.
        return GitHubUrls.TryParsePullRequest(url, out pr);
    }
}
