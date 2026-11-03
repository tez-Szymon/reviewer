using Reviewer.Agents.Cli;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// Config and providers for <c>reviewer serve</c>'s scheduler. One provider per account while its settings stay the
/// same, so the token owner is looked up once; a token that couldn't be found is looked for again on the next poll.
/// </summary>
internal sealed class PollSource(ReviewerPaths paths, IProcessRunner processes) : IPollSource, IDisposable
{
    private readonly Dictionary<string, AccountConnection> _connections = new(StringComparer.Ordinal);

    public Task<ReviewerConfig> LoadConfigAsync(CancellationToken ct) => ConfigLoader.LoadAsync(paths.Config, ct);

    public async Task<IRepositoryProvider> GetProviderAsync(AccountConfig account, CancellationToken ct)
    {
        // An edited account (another host or token reference) gets a fresh connection.
        if (_connections.TryGetValue(account.Id, out var cached))
        {
            if (cached.Account == account)
            {
                return cached.Provider;
            }

            cached.Dispose();
            _connections.Remove(account.Id);
        }

        var connection = await AccountConnection.OpenAsync(processes, account, ct).ConfigureAwait(false);
        _connections[account.Id] = connection;
        return connection.Provider;
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
    }
}
