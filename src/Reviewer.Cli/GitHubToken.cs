using System.ComponentModel;
using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli;

/// <summary>
/// Finds the GitHub token until the OS keychain arrives in M3: the account's <c>env:NAME</c> reference when it has
/// one, otherwise <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c>, then the GitHub CLI's own login.
/// </summary>
internal static class GitHubToken
{
    private const string EnvPrefix = "env:";

    public static async Task<string?> ResolveAsync(IProcessRunner runner, AccountConfig account, CancellationToken ct)
    {
        if (account.Token is { } reference)
        {
            // The loader accepts env: references only.
            return Environment.GetEnvironmentVariable(reference[EnvPrefix.Length..]) is { Length: > 0 } configured ? configured : null;
        }

        foreach (var name in (string[])["GITHUB_TOKEN", "GH_TOKEN"])
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } token)
            {
                return token;
            }
        }

        try
        {
            var result = await runner.RunAsync(
                new ProcessSpec("gh", ["auth", "token", "--hostname", account.Host.Host], Environment.CurrentDirectory, new Dictionary<string, string?>()),
                ct).ConfigureAwait(false);
            return result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } token ? token : null;
        }
        catch (Exception e) when (e is Win32Exception || e.InnerException is Win32Exception)
        {
            return null; // gh isn't installed.
        }
    }

    public static string Missing(AccountConfig account) => account.Token is { } reference
        ? $"No GitHub token: account '{account.Id}' reads it from {reference[EnvPrefix.Length..]}, which is not set."
        : "No GitHub token: set GITHUB_TOKEN or log in with 'gh auth login'.";
}
