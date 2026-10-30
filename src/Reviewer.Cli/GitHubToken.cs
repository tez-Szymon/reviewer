using System.ComponentModel;
using Reviewer.Agents.Cli;

namespace Reviewer.Cli;

/// <summary>
/// Finds the GitHub token until the OS keychain arrives in M3: <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c>, then the GitHub
/// CLI's own login.
/// </summary>
internal static class GitHubToken
{
    public static async Task<string?> ResolveAsync(IProcessRunner runner, CancellationToken ct)
    {
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
                new ProcessSpec("gh", ["auth", "token", "--hostname", "github.com"], Environment.CurrentDirectory, new Dictionary<string, string?>()),
                ct).ConfigureAwait(false);
            return result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } token ? token : null;
        }
        catch (Exception e) when (e is Win32Exception || e.InnerException is Win32Exception)
        {
            return null; // gh isn't installed.
        }
    }

    public const string Missing = "No GitHub token: set GITHUB_TOKEN or log in with 'gh auth login'.";
}
