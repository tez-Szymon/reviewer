using Reviewer.Agents.Cli;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Git;
using Reviewer.Providers.GitHub;
using Reviewer.Providers.GitLab;

namespace Reviewer.Cli;

/// <summary>
/// One account's provider and token, and where git fetches its PRs from: the one place that knows which adapter goes
/// with which provider. Disposing it releases the HTTP client.
/// </summary>
internal sealed class AccountConnection : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _token;

    private AccountConnection(AccountConfig account, IRepositoryProvider provider, HttpClient http, string token)
    {
        Account = account;
        Provider = provider;
        _http = http;
        _token = token;
    }

    public AccountConfig Account { get; }

    public IRepositoryProvider Provider { get; }

    /// <exception cref="InvalidOperationException">No token was found, or its keychain item couldn't be read (<see cref="KeychainException"/>).</exception>
    public static async Task<AccountConnection> OpenAsync(IProcessRunner processes, AccountConfig account, CancellationToken ct)
    {
        var token = await AccountToken.ResolveAsync(processes, account, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(AccountToken.Missing(account));
        if (account.Provider == AccountConfig.GitLab)
        {
            var gitlab = GitLabProvider.CreateHttpClient(account.Host, token);
            return new AccountConnection(account, new GitLabProvider(gitlab), gitlab, token);
        }

        var github = GitHubProvider.CreateHttpClient(token);
        return new AccountConnection(account, new GitHubProvider(github), github, token);
    }

    /// <summary>Where git fetches <paramref name="pr"/>'s head from, with this account's token.</summary>
    public ReviewSource Source(PullRequestRef pr) => Account.Provider == AccountConfig.GitLab
        // GitLab takes any username with a token; oauth2 is the one its docs use.
        ? new ReviewSource(GitLabUrls.CloneUrl(Account.Host, pr.Repo), GitLabUrls.MergeRequestHeadRef(pr.Number), new GitCredential("oauth2", _token))
        : new ReviewSource(GitHubUrls.CloneUrl(pr.Repo), GitHubUrls.PullHeadRef(pr.Number), new GitCredential("x-access-token", _token));

    public void Dispose() => _http.Dispose();
}
