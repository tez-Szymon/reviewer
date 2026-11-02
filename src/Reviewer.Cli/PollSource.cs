using Reviewer.Agents.Cli;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Providers.GitHub;

namespace Reviewer.Cli;

/// <summary>
/// Config and providers for <c>reviewer serve</c>'s scheduler. One provider per account for the engine's lifetime, so
/// the token owner is looked up once; a token that couldn't be found is looked for again on the next poll.
/// </summary>
internal sealed class PollSource(ReviewerPaths paths, IProcessRunner processes) : IPollSource, IDisposable
{
    private readonly Dictionary<string, (HttpClient Http, GitHubProvider Provider)> _providers = new(StringComparer.Ordinal);

    public Task<ReviewerConfig> LoadConfigAsync(CancellationToken ct) => ConfigLoader.LoadAsync(paths.Config, ct);

    public async Task<IRepositoryProvider> GetProviderAsync(AccountConfig account, CancellationToken ct)
    {
        if (_providers.TryGetValue(account.Id, out var cached))
        {
            return cached.Provider;
        }

        if (account.Provider != "github" || !string.Equals(account.Host.Host, GitHubUrls.GitHubCom.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only github.com accounts can be polled, not {account.Provider} on {account.Host.Host}.");
        }

        if (await GitHubToken.ResolveAsync(processes, account, ct).ConfigureAwait(false) is not { } token)
        {
            throw new InvalidOperationException(GitHubToken.Missing(account));
        }

        var http = GitHubProvider.CreateHttpClient(token);
        var provider = new GitHubProvider(http);
        _providers[account.Id] = (http, provider);
        return provider;
    }

    public void Dispose()
    {
        foreach (var (http, _) in _providers.Values)
        {
            http.Dispose();
        }
    }
}
