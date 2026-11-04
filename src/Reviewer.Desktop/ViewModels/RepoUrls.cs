using Reviewer.Core.Providers;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.ViewModels;

/// <summary>Reads a pasted repo URL against the configured accounts: its host says which account watches it.</summary>
internal static class RepoUrls
{
    /// <summary>
    /// The account and repo path for <paramref name="input"/>: a repo's URL, or a page under it (a PR, a file), or a
    /// bare <c>owner/name</c> when there's one account.
    /// </summary>
    /// <returns>Null when it's understood, otherwise why not.</returns>
    public static string? TryParse(string input, IReadOnlyList<AccountConfig> accounts, out AccountConfig account, out string repo)
    {
        account = null!;
        repo = "";
        input = input.Trim();
        if (input.Length == 0)
        {
            return "Paste a repo's URL.";
        }

        string path;
        if (!input.Contains("://", StringComparison.Ordinal))
        {
            if (accounts.Count != 1)
            {
                return "Paste the repo's URL, so its host says which account watches it.";
            }

            account = accounts[0];
            path = input;
        }
        else
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var url))
            {
                return $"'{input}' isn't a URL.";
            }

            // A self-hosted GitLab may be served from a path; the account whose path is the longest match wins.
            var match = accounts
                .Where(a => ReviewerConfig.SameHost(a.Host, url) && Under(url.AbsolutePath, a.Host.AbsolutePath))
                .MaxBy(a => a.Host.AbsolutePath.TrimEnd('/').Length);
            if (match is null)
            {
                return $"No account in the config for {url.Host}; add one in Settings first.";
            }

            account = match;
            path = Uri.UnescapeDataString(url.AbsolutePath[match.Host.AbsolutePath.TrimEnd('/').Length..]);
        }

        path = path.Trim('/');
        var gitlab = account.Provider == AccountConfig.GitLab;
        if (gitlab && path.IndexOf("/-/", StringComparison.Ordinal) is var dash and >= 0)
        {
            path = path[..dash];
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!gitlab && segments.Length > 2)
        {
            segments = segments[..2];
        }

        repo = string.Join('/', segments);
        return segments.Length >= 2 && RepoRef.TryParse(account.Host, repo, out _)
            ? null
            : $"'{input}' doesn't name a repo; expected {(gitlab ? "group/project" : "owner/name")}.";
    }

    private static bool Under(string path, string prefix)
    {
        prefix = prefix.TrimEnd('/');
        return prefix.Length == 0 || path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
