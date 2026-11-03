using System.ComponentModel;
using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// Finds an account's token: its reference when it has one (an environment variable or a keychain item). Otherwise,
/// on GitHub <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c>, then the GitHub CLI's own login; on GitLab <c>GITLAB_TOKEN</c>.
/// </summary>
internal static class AccountToken
{
    /// <exception cref="KeychainException">The account's keychain item couldn't be read.</exception>
    public static Task<string?> ResolveAsync(IProcessRunner runner, AccountConfig account, CancellationToken ct) =>
        ResolveAsync(runner, new OsKeychain(runner), account, ct);

    /// <exception cref="KeychainException">The account's keychain item couldn't be read.</exception>
    public static async Task<string?> ResolveAsync(IProcessRunner runner, IKeychain keychain, AccountConfig account, CancellationToken ct)
    {
        if (account.Token is { } token)
        {
            // The loader accepts only references, so an account's token always parses.
            return TokenReference.Parse(token) switch
            {
                TokenReference.Env env => Variable(env.Name),
                TokenReference.Keychain item => await keychain.ReadAsync(item, ct).ConfigureAwait(false),
                _ => null,
            };
        }

        if (account.Provider == AccountConfig.GitLab)
        {
            return Variable("GITLAB_TOKEN");
        }

        if ((Variable("GITHUB_TOKEN") ?? Variable("GH_TOKEN")) is { } configured)
        {
            return configured;
        }

        try
        {
            var result = await runner.RunAsync(
                new ProcessSpec("gh", ["auth", "token", "--hostname", account.Host.Host], Environment.CurrentDirectory, new Dictionary<string, string?>()),
                ct).ConfigureAwait(false);
            return result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } login ? login : null;
        }
        catch (Exception e) when (e is Win32Exception || e.InnerException is Win32Exception)
        {
            return null; // gh isn't installed.
        }
    }

    public static string Missing(AccountConfig account)
    {
        var platform = account.Provider == AccountConfig.GitLab ? "GitLab" : "GitHub";
        return account.Token is { } token
            ? TokenReference.Parse(token) switch
            {
                TokenReference.Keychain item =>
                    $"No {platform} token: account '{account.Id}' reads it from {item.Describe()}, which doesn't exist. Add it with: {OsKeychain.AddCommand(item)}",
                var reference => $"No {platform} token: account '{account.Id}' reads it from {reference.Describe()}, which is not set.",
            }
            : account.Provider == AccountConfig.GitLab
                ? $"No GitLab token: set GITLAB_TOKEN, or give account '{account.Id}' a token reference in {ConfigLoader.FileName}."
                : "No GitHub token: set GITHUB_TOKEN or log in with 'gh auth login'.";
    }

    private static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
