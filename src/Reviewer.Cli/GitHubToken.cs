using System.ComponentModel;
using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// Finds the GitHub token: the account's reference when it has one (an environment variable or a keychain item),
/// otherwise <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c>, then the GitHub CLI's own login.
/// </summary>
internal static class GitHubToken
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
                TokenReference.Env env => Environment.GetEnvironmentVariable(env.Name) is { Length: > 0 } configured ? configured : null,
                TokenReference.Keychain item => await keychain.ReadAsync(item, ct).ConfigureAwait(false),
                _ => null,
            };
        }

        foreach (var name in (string[])["GITHUB_TOKEN", "GH_TOKEN"])
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } configured)
            {
                return configured;
            }
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

    public static string Missing(AccountConfig account) => account.Token is { } token
        ? TokenReference.Parse(token) switch
        {
            TokenReference.Keychain item =>
                $"No GitHub token: account '{account.Id}' reads it from {item.Describe()}, which doesn't exist. Add it with: {OsKeychain.AddCommand(item)}",
            var reference => $"No GitHub token: account '{account.Id}' reads it from {reference.Describe()}, which is not set.",
        }
        : "No GitHub token: set GITHUB_TOKEN or log in with 'gh auth login'.";
}
