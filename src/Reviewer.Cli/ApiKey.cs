using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// Finds an API agent's key: its <c>apiKey</c> reference when it has one (an environment variable or a keychain
/// item), otherwise <c>ANTHROPIC_API_KEY</c>. The key is read when a review or health check needs it, never stored.
/// </summary>
internal static class ApiKey
{
    public const string DefaultVariable = "ANTHROPIC_API_KEY";

    /// <summary>The key, or null when it's not set or the keychain item can't be read.</summary>
    public static async Task<string?> ResolveAsync(IKeychain keychain, AgentSettings settings, CancellationToken ct)
    {
        try
        {
            return Reference(settings) switch
            {
                TokenReference.Env env => Variable(env.Name),
                TokenReference.Keychain item => await keychain.ReadAsync(item, ct).ConfigureAwait(false) is { Length: > 0 } key ? key : null,
                _ => null,
            };
        }
        catch (KeychainException)
        {
            return null;
        }
    }

    /// <summary>Where the key was looked for, and how to add it.</summary>
    public static string Missing(string agentId, AgentSettings settings) => Reference(settings) switch
    {
        TokenReference.Keychain item =>
            $"No API key for {agentId}: it's read from {item.Describe()}, which doesn't exist or can't be read. Add it with: {OsKeychain.AddCommand(item)}",
        var reference when settings.ApiKey is null =>
            $"No API key for {agentId}: set {DefaultVariable}, or give agents.{agentId}.apiKey a reference in {ConfigLoader.FileName}.",
        var reference => $"No API key for {agentId}: it's read from {reference.Describe()}, which is not set.",
    };

    // The loader accepts only references, so a configured one always parses.
    private static TokenReference Reference(AgentSettings settings) =>
        settings.ApiKey is { } key ? TokenReference.Parse(key) : new TokenReference.Env(DefaultVariable);

    private static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
