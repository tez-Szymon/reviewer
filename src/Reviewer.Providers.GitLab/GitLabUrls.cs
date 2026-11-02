using System.Globalization;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitLab;

/// <summary>
/// URLs for gitlab.com and self-hosted GitLab alike. The host is the account's, and may carry a path when GitLab is
/// served from one (<c>https://example.com/gitlab</c>).
/// </summary>
public static class GitLabUrls
{
    public static readonly Uri GitLabCom = new("https://gitlab.com");

    /// <summary>The REST API root, e.g. <c>https://gitlab.com/api/v4/</c>.</summary>
    public static Uri ApiBase(Uri host) => new($"{Root(host)}/api/v4/");

    /// <summary>HTTPS URL git fetches the project from.</summary>
    public static string CloneUrl(Uri host, RepoRef repo) => $"{Root(host)}/{repo.Owner}/{repo.Name}.git";

    /// <summary>The ref GitLab keeps at a merge request's head commit, fetchable even for MRs from forks.</summary>
    public static string MergeRequestHeadRef(int iid) => $"refs/merge-requests/{iid.ToString(CultureInfo.InvariantCulture)}/head";

    /// <summary>
    /// Parses <c>{host}/{group}/[{subgroup}/…]{project}/-/merge_requests/{iid}</c>, ignoring any trailing path, query or
    /// fragment. The URL must be on <paramref name="host"/>, path included.
    /// </summary>
    public static bool TryParseMergeRequest(Uri url, Uri host, out PullRequestRef pr)
    {
        pr = null!;
        if (!url.IsAbsoluteUri
            || Uri.Compare(url, host, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var hostSegments = host.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < hostSegments.Length
            || !segments.Take(hostSegments.Length).SequenceEqual(hostSegments, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = segments[hostSegments.Length..];
        var dash = Array.IndexOf(path, "-");
        if (dash < 2 // A project is at least group/project.
            || path.Length < dash + 3
            || path[dash + 1] != "merge_requests"
            || !int.TryParse(path[dash + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var iid)
            || iid <= 0)
        {
            return false;
        }

        pr = new PullRequestRef(new RepoRef(host, string.Join('/', path[..(dash - 1)]), path[dash - 1]), iid);
        return true;
    }

    private static string Root(Uri host) => host.GetLeftPart(UriPartial.Path).TrimEnd('/');
}
