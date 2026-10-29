using System.Globalization;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub;

public static class GitHubUrls
{
    public static readonly Uri GitHubCom = new("https://github.com");

    /// <summary>Parses <c>https://github.com/{owner}/{repo}/pull/{number}</c>, ignoring any trailing path, query or fragment.</summary>
    public static bool TryParsePullRequest(Uri url, out PullRequestRef pr)
    {
        pr = null!;
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!url.IsAbsoluteUri
            || !string.Equals(url.Host, GitHubCom.Host, StringComparison.OrdinalIgnoreCase)
            || segments.Length < 4
            || segments[2] != "pull"
            || !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        pr = new PullRequestRef(new RepoRef(GitHubCom, segments[0], segments[1]), number);
        return true;
    }
}
