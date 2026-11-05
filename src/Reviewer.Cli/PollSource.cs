using Reviewer.Agents.Cli;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// Config and providers for <c>reviewer serve</c>'s scheduler and local API. One provider per account while its settings
/// and token stay the same, so the token owner is looked up once. The token is looked up on every call, so a token
/// replaced where it's kept (a keychain item the app rewrote, say) is used from the next call on, and one that couldn't
/// be found is looked for again. The scheduler and API requests share it, so it hands out providers one caller at a time.
/// </summary>
internal sealed class PollSource(ReviewerPaths paths, IProcessRunner processes, IKeychain keychain) : IPollSource, IDisposable
{
    private readonly Dictionary<string, AccountConnection> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PollSource(ReviewerPaths paths, IProcessRunner processes)
        : this(paths, processes, new OsKeychain(processes))
    {
    }

    public Task<ReviewerConfig> LoadConfigAsync(CancellationToken ct) => ConfigLoader.LoadAsync(paths.Config, ct);

    public async Task<IRepositoryProvider> GetProviderAsync(AccountConfig account, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A keychain that can't be read throws KeychainException, an InvalidOperationException as the interface says.
            var token = await AccountToken.ResolveAsync(processes, keychain, account, ct).ConfigureAwait(false);

            // An edited account (another host or token reference) or a new token gets a fresh connection. The old one
            // may still be in use by a request that holds its provider, so it's left to the garbage collector rather
            // than disposed.
            if (_connections.TryGetValue(account.Id, out var cached))
            {
                if (cached.Account == account && cached.Uses(token))
                {
                    return cached.Provider;
                }

                _connections.Remove(account.Id);
            }

            var connection = AccountConnection.Open(account, token);
            _connections[account.Id] = connection;
            return connection.Provider;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
    }
}
