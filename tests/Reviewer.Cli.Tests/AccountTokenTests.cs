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

public class ApiKeyTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static AgentSettings Settings(string? apiKey) => new(1, 15, "claude-opus-5-5", apiKey);

    [Fact]
    public async Task Resolve_KeychainReference_ReadsTheItem()
    {
        var keychain = new FakeKeychain(new() { ["reviewer/anthropic"] = "sk-ant-keychain" });

        Assert.Equal("sk-ant-keychain", await ApiKey.ResolveAsync(keychain, Settings("keychain:reviewer/anthropic"), Ct));
        Assert.Null(await ApiKey.ResolveAsync(new FakeKeychain([]), Settings("keychain:reviewer/anthropic"), Ct));
    }

    [Fact]
    public async Task Resolve_EnvReference_ReadsThatVariable_AndWithoutOne_TheUsualVariable()
    {
        var name = $"REVIEWER_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "sk-ant-env");
        try
        {
            Assert.Equal("sk-ant-env", await ApiKey.ResolveAsync(new FakeKeychain([]), Settings($"env:{name}"), Ct));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Assert.Equal(Environment.GetEnvironmentVariable(ApiKey.DefaultVariable) is { Length: > 0 } set ? set : null,
            await ApiKey.ResolveAsync(new FakeKeychain([]), Settings(null), Ct));
    }

    [Fact]
    public void Missing_SaysWhereTheKeyWasLookedFor()
    {
        Assert.StartsWith("No API key for anthropic-api: set ANTHROPIC_API_KEY, or give agents.anthropic-api.apiKey a reference",
            ApiKey.Missing("anthropic-api", Settings(null)), StringComparison.Ordinal);
        Assert.Equal("No API key for anthropic-api: it's read from environment variable WORK_KEY, which is not set.",
            ApiKey.Missing("anthropic-api", Settings("env:WORK_KEY")));
        Assert.Contains(OsKeychain.AddCommand(new TokenReference.Keychain("reviewer", "anthropic")),
            ApiKey.Missing("anthropic-api", Settings("keychain:reviewer/anthropic")), StringComparison.Ordinal);
    }
}
