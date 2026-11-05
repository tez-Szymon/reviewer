using Reviewer.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.Tests;

/// <summary>A keychain held in a dictionary keyed by target, refusing what the real one refuses.</summary>
internal sealed class FakeKeychain : IKeychain
{
    public Dictionary<string, string> Items { get; } = [];

    public Task<string?> ReadAsync(TokenReference.Keychain item, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(item.Target));

    public Task WriteAsync(TokenReference.Keychain item, string secret, CancellationToken ct)
    {
        if (secret.Any(char.IsWhiteSpace))
        {
            return Task.FromException(new KeychainException("That doesn't look like a token: tokens have no spaces, quotes or line breaks."));
        }

        Items[item.Target] = secret;
        return Task.CompletedTask;
    }
}
