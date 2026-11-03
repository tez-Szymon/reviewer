using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli.Tests;

public class AccountTokenTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static AccountConfig Account(string? token) => new("gh-main", "github", new Uri("https://github.com"), token);

    [Fact]
    public async Task Resolve_KeychainReference_ReadsTheItemAndNothingElse()
    {
        var runner = new FakeRunner(new ProcessResult(0, "from-gh", ""));
        var keychain = new FakeKeychain(new() { ["reviewer/gh-main"] = "ghp_keychain" });

        Assert.Equal("ghp_keychain", await AccountToken.ResolveAsync(runner, keychain, Account("keychain:reviewer/gh-main"), Ct));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Resolve_MissingKeychainItem_DoesNotFallBackToGh()
    {
        var runner = new FakeRunner(new ProcessResult(0, "from-gh", ""));

        Assert.Null(await AccountToken.ResolveAsync(runner, new FakeKeychain([]), Account("keychain:reviewer/gh-main"), Ct));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Missing_KeychainItem_SaysHowToAddIt()
    {
        var message = AccountToken.Missing(Account("keychain:reviewer/gh-main"));

        Assert.StartsWith("No GitHub token: account 'gh-main' reads it from keychain item reviewer/gh-main, which doesn't exist.", message, StringComparison.Ordinal);
        Assert.Contains(OsKeychain.AddCommand(new TokenReference.Keychain("reviewer", "gh-main")), message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_EnvReference_NamesTheVariable()
    {
        Assert.Equal(
            "No GitHub token: account 'gh-main' reads it from environment variable REVIEWER_GH, which is not set.",
            AccountToken.Missing(Account("env:REVIEWER_GH")));
    }

    [Fact]
    public async Task Resolve_GitLabWithoutReference_NeverAsksGh()
    {
        var runner = new FakeRunner(new ProcessResult(0, "from-gh", ""));
        var gitlab = new AccountConfig("work", AccountConfig.GitLab, new Uri("https://git.acme.test"), null);

        var token = await AccountToken.ResolveAsync(runner, new FakeKeychain([]), gitlab, Ct);

        Assert.Equal(Environment.GetEnvironmentVariable("GITLAB_TOKEN") is { Length: > 0 } set ? set : null, token);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Missing_GitLab_NamesThePlatformAndWhereToSetIt()
    {
        Assert.Equal(
            "No GitLab token: set GITLAB_TOKEN, or give account 'work' a token reference in config.yaml.",
            AccountToken.Missing(new AccountConfig("work", AccountConfig.GitLab, new Uri("https://git.acme.test"), null)));
        Assert.StartsWith(
            "No GitLab token: account 'work' reads it from keychain item reviewer/work",
            AccountToken.Missing(new AccountConfig("work", AccountConfig.GitLab, new Uri("https://git.acme.test"), "keychain:reviewer/work")),
            StringComparison.Ordinal);
    }
}
