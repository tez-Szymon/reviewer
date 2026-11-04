using System.Text.Json.Nodes;
using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.Tests;

/// <summary>The Repos and Settings pages, editing <c>config.yaml</c> through the fake engine as the real one would.</summary>
public class ConfigPageTests
{
    private const string TwoAccounts = """
        accounts:
          - { id: gh, provider: github }
          - { id: gl, provider: gitlab, host: https://git.example.com/gitlab }

        """;

    private readonly FakeEngine _engine = new();

    private ReposViewModel Repos() => new(_engine, new ConfigDocument(_engine));

    private SettingsViewModel Settings() => new(_engine, new ConfigDocument(_engine));

    private static SettingField Field(IEnumerable<SettingSection> sections, string path) =>
        sections.SelectMany(s => s.Fields).Single(f => f.Spec.Path == path);

    [Fact]
    public async Task Repos_ListsTheWatchedRepos_WithAccountAgentAndMode()
    {
        _engine.ConfigYaml = "defaults: { review: { mode: approve } }\nrepos:\n  - repo: acme/api\n  - repo: acme/web\n    agent: codex\n    overrides: { review: { mode: gate } }\n";
        var repos = Repos();

        await repos.LoadAsync();

        Assert.Equal(["acme/api", "acme/web"], repos.Watched.Select(r => r.Repo));
        Assert.Equal(["github · claude-code · approve", "github · codex · gate"], repos.Watched.Select(r => r.Details));
        Assert.True(repos.CanEdit);
    }

    [Fact]
    public async Task Repos_AddByUrl_AddsAnEntry_AndKeepsTheRestOfTheFile()
    {
        _engine.ConfigYaml = "# mine\nrepos:\n  - repo: acme/web   # the site\n";
        var repos = Repos();
        await repos.LoadAsync();

        repos.AddUrl = "https://github.com/acme/api/pull/3";
        await repos.AddCommand.ExecuteAsync(null);

        Assert.Null(repos.Error);
        Assert.Equal("# mine\nrepos:\n  - repo: acme/web   # the site\n  - { repo: acme/api }\n", _engine.ConfigYaml);
        Assert.Equal(["acme/web", "acme/api"], repos.Watched.Select(r => r.Repo));
        Assert.Equal("", repos.AddUrl);
    }

    [Fact]
    public async Task Repos_AddByUrl_WithSeveralAccounts_NamesTheAccount()
    {
        _engine.ConfigYaml = TwoAccounts;
        var repos = Repos();
        await repos.LoadAsync();

        repos.AddUrl = "https://git.example.com/gitlab/platform/finance/billing/-/merge_requests/4";
        await repos.AddCommand.ExecuteAsync(null);

        Assert.Null(repos.Error);
        Assert.EndsWith("repos:\n  - { repo: platform/finance/billing, account: gl }\n", _engine.ConfigYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repos_AddByUrl_RefusesWhatItCantPlace()
    {
        _engine.ConfigYaml = "repos:\n  - repo: acme/api\n";
        var repos = Repos();
        await repos.LoadAsync();

        repos.AddUrl = "https://bitbucket.org/acme/api";
        await repos.AddCommand.ExecuteAsync(null);
        Assert.Equal("No account in the config for bitbucket.org; add one in Settings first.", repos.Error);

        repos.AddUrl = "https://github.com/ACME/api";
        await repos.AddCommand.ExecuteAsync(null);
        Assert.Equal("ACME/api is already watched.", repos.Error);
        Assert.Empty(_engine.Saves);
    }

    [Theory]
    [InlineData("https://github.com/acme/api", "gh", "acme/api")]
    [InlineData("https://github.com/acme/api.git", "gh", "acme/api")]
    [InlineData("https://github.com/acme/api/blob/main/README.md", "gh", "acme/api")]
    [InlineData("https://git.example.com/gitlab/platform/billing", "gl", "platform/billing")]
    [InlineData("https://git.example.com/gitlab/platform/finance/billing/-/tree/main", "gl", "platform/finance/billing")]
    public void RepoUrls_ReadTheAccountAndPath(string url, string account, string repo)
    {
        var accounts = ConfigLoader.Parse(TwoAccounts).Accounts;

        Assert.Null(RepoUrls.TryParse(url, accounts, out var found, out var path));
        Assert.Equal(account, found.Id);
        Assert.Equal(repo, path);
    }

    [Theory]
    [InlineData("acme/api", "Paste the repo's URL, so its host says which account watches it.")]
    [InlineData("https://github.com/acme", "'https://github.com/acme' doesn't name a repo; expected owner/name.")]
    [InlineData("https://git.example.com/other/billing", "No account in the config for git.example.com; add one in Settings first.")]
    public void RepoUrls_SayWhyNot(string url, string problem)
    {
        var accounts = ConfigLoader.Parse(TwoAccounts).Accounts;

        Assert.Equal(problem, RepoUrls.TryParse(url, accounts, out _, out _));
    }

    [Fact]
    public void RepoUrls_TakeAPath_WhenThereIsOneAccount()
    {
        Assert.Null(RepoUrls.TryParse("acme/api", ReviewerConfig.Default.Accounts, out var account, out var repo));
        Assert.Equal(("github", "acme/api"), (account.Id, repo));
    }

    [Fact]
    public async Task Repos_Overrides_ShowWhatTheRepoSetsAndWhatItInherits()
    {
        _engine.ConfigYaml = "defaults: { review: { mode: approve } }\nrepos:\n  - repo: acme/api\n    overrides: { review: { dryRun: true } }\n";
        var repos = Repos();
        await repos.LoadAsync();

        repos.Selected = repos.Watched[0];

        var mode = Field(repos.Overrides, "review.mode");
        Assert.False(mode.IsSet);
        Assert.Equal("approve", mode.Value);
        Assert.Equal("inherits: approve", mode.InheritedText);
        var dryRun = Field(repos.Overrides, "review.dryRun");
        Assert.True(dryRun.IsSet);
        Assert.Equal("true", dryRun.Value);
        Assert.Equal("inherits: false", dryRun.InheritedText);
        Assert.Equal("claude-code", repos.Agent!.Value);
        Assert.Equal("**/*.lock\n**/dist/**\n**/*.min.js", Field(repos.Overrides, "review.ignorePaths").Value);
    }

    [Fact]
    public async Task Repos_SavingOverrides_SetsAndRemovesOnlyWhatChanged()
    {
        _engine.ConfigYaml = "repos:\n  - repo: acme/web\n  - repo: acme/api   # payments\n    overrides:\n      review:\n        mode: gate\n";
        var repos = Repos();
        await repos.LoadAsync();
        repos.Selected = repos.Watched[1];

        Field(repos.Overrides, "review.mode").IsSet = false;
        var paths = Field(repos.Overrides, "review.ignorePaths");
        paths.IsSet = true;
        paths.Value = "**/gen/**\n\n  vendor/**  ";
        repos.Agent!.IsSet = true;
        repos.Agent.Value = "codex";
        await repos.SaveCommand.ExecuteAsync(null);

        Assert.Null(repos.Error);
        Assert.Equal(
            ["set repos[1].agent \"codex\"", "remove repos[1].overrides.review.mode ", "set repos[1].overrides.review.ignorePaths [\"**/gen/**\",\"vendor/**\"]"],
            _engine.Saves.Single().Select(e => $"{e.Op.ToString().ToLowerInvariant()} {e.Path} {e.Value?.ToJsonString()}"));
        Assert.Equal(
            // Removing review's only key leaves {}, so the new one goes into that map in flow style.
            "repos:\n  - repo: acme/web\n  - repo: acme/api   # payments\n    overrides:\n      review: { ignorePaths: [\"**/gen/**\", \"vendor/**\"] }\n    agent: codex\n",
            _engine.ConfigYaml);
        Assert.Equal("acme/api", repos.Selected?.Repo);
        Assert.Equal("github · codex · comment", repos.Watched[1].Details);
    }

    [Fact]
    public async Task Repos_Save_WithNothingChanged_SendsNothing()
    {
        _engine.ConfigYaml = "repos:\n  - repo: acme/api\n";
        var repos = Repos();
        await repos.LoadAsync();
        repos.Selected = repos.Watched[0];

        await repos.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_engine.Saves);
    }

    [Fact]
    public async Task Repos_StopWatching_RemovesTheEntry()
    {
        _engine.ConfigYaml = "repos:\n  - repo: acme/web\n  - repo: acme/api\n";
        var repos = Repos();
        await repos.LoadAsync();
        repos.Selected = repos.Watched[1];

        await repos.RemoveCommand.ExecuteAsync(repos.Watched[1]);

        Assert.Equal("repos:\n  - repo: acme/web\n", _engine.ConfigYaml);
        Assert.Null(repos.Selected);
        Assert.False(repos.HasSelection);
    }

    [Fact]
    public async Task Repos_AHandEditMeanwhile_SavesNothing_AndShowsTheFileAsItIsNow()
    {
        _engine.ConfigYaml = "repos:\n  - repo: acme/api\n";
        var repos = Repos();
        await repos.LoadAsync();
        repos.Selected = repos.Watched[0];
        _engine.ConfigYaml = "repos:\n  - repo: acme/api\n  - repo: acme/web\n";

        var mode = Field(repos.Overrides, "review.mode");
        mode.IsSet = true;
        mode.Value = "gate";
        await repos.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("config.yaml was changed elsewhere", repos.Error, StringComparison.Ordinal);
        Assert.Empty(_engine.Saves);
        Assert.Equal(["acme/api", "acme/web"], repos.Watched.Select(r => r.Repo));
    }

    [Fact]
    public async Task Repos_AnInvalidFile_ShowsItsProblems_AndCantBeEdited()
    {
        _engine.ConfigYaml = "defaults: { review: { mode: nope } }\n";
        var repos = Repos();

        await repos.LoadAsync();

        Assert.False(repos.CanEdit);
        Assert.Contains(repos.Problems, p => p.Contains("defaults.review.mode", StringComparison.Ordinal));
        Assert.Empty(repos.Watched);
    }

    [Fact]
    public async Task Repos_Browse_ListsTheAccountsRepos_AndWatchesOne()
    {
        _engine.ConfigYaml = TwoAccounts;
        _engine.RepoChoices.AddRange([new RepoChoice("platform/billing", false), new RepoChoice("platform/web", true)]);
        var repos = Repos();
        await repos.LoadAsync();

        repos.BrowseAccount = "gl";
        repos.BrowseOwner = " platform ";
        await repos.BrowseCommand.ExecuteAsync(null);
        await repos.WatchChoiceCommand.ExecuteAsync(repos.Choices[0]);

        Assert.Contains("repos gl platform", _engine.Calls);
        Assert.Equal([true, true], repos.Choices.Select(c => c.Watched));
        Assert.EndsWith("repos:\n  - { repo: platform/billing, account: gl }\n", _engine.ConfigYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_SavesTheGlobalPolicyAndAgents_AndUntickingGoesBackToBuiltIn()
    {
        _engine.ConfigYaml = "# mine\ndefaults:\n  review:\n    minConfidence: 0.8   # strict\n";
        var settings = Settings();
        await settings.LoadAsync();

        Assert.Equal("inherits: 0.6", Field(settings.Policy, "review.minConfidence").InheritedText);
        Field(settings.Policy, "review.minConfidence").IsSet = false;
        var mode = Field(settings.Policy, "review.mode");
        mode.IsSet = true;
        mode.Value = "gate-merge";
        var model = Field(settings.AgentSettings, "codex.model");
        model.IsSet = true;
        model.Value = "gpt-5.5";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.Null(settings.Error);
        var config = ConfigLoader.Parse(_engine.ConfigYaml);
        Assert.Equal(0.6, config.Defaults.Review.MinConfidence);
        Assert.Equal(Reviewer.Core.Policy.ReviewMode.GateMerge, config.Defaults.Review.Mode);
        Assert.Equal("gpt-5.5", config.Agents.Codex.Model);
        Assert.StartsWith("# mine\n", _engine.ConfigYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_AValueOfTheWrongKind_SavesNothing()
    {
        var settings = Settings();
        await settings.LoadAsync();

        var poll = Field(settings.Policy, "triggers.pollSeconds");
        poll.IsSet = true;
        poll.Value = "soon";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Poll every (seconds) needs a whole number.", settings.Error);
        Assert.Empty(_engine.Saves);
    }

    [Fact]
    public async Task Settings_AValueTheEngineRefuses_ShowsWhy()
    {
        var settings = Settings();
        await settings.LoadAsync();

        var poll = Field(settings.Policy, "triggers.pollSeconds");
        poll.IsSet = true;
        poll.Value = "5";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.Contains("pollSeconds", settings.Error, StringComparison.Ordinal);
        Assert.Equal("", _engine.ConfigYaml);
    }

    [Fact]
    public async Task Settings_Accounts_ShowTheImplicitOne_ThenAnAddedOne_WithTheirStatus()
    {
        _engine.AccountStatuses["gl-work"] = new AccountStatus("gl-work", false, null, "GITLAB_TOKEN isn't set.");
        var settings = Settings();
        await settings.LoadAsync();

        Assert.True(settings.ImplicitAccount);
        Assert.False(Assert.Single(settings.Accounts).CanRemove);

        settings.NewAccountId = "gl-work";
        settings.NewProvider = AccountConfig.GitLab;
        settings.NewHost = "https://gitlab.example.com";
        await settings.AddAccountCommand.ExecuteAsync(null);

        Assert.Null(settings.Error);
        Assert.Equal("accounts:\n  - { id: gl-work, provider: gitlab, host: \"https://gitlab.example.com\" }\n", _engine.ConfigYaml);
        var account = Assert.Single(settings.Accounts);
        Assert.True(account.CanRemove);
        Assert.False(settings.ImplicitAccount);
        Assert.Equal("GITLAB_TOKEN isn't set.", account.Status);
        Assert.Equal("", settings.NewAccountId);
    }

    [Fact]
    public async Task Settings_RemovingAnAccountARepoUses_IsRefused()
    {
        _engine.ConfigYaml = TwoAccounts + "repos:\n  - { repo: platform/billing, account: gl }\n";
        var settings = Settings();
        await settings.LoadAsync();

        await settings.RemoveAccountCommand.ExecuteAsync(settings.Accounts[1]);

        Assert.NotNull(settings.Error);
        Assert.Empty(_engine.Saves);
        Assert.Equal(2, settings.Accounts.Count);
    }

    [Fact]
    public async Task Settings_ListsAgentHealth()
    {
        _engine.Agents.AddRange([new AgentStatus("claude-code", true, true, true, "2.1.312", null), new AgentStatus("codex", false, true, false, "0.162.1", null)]);
        var settings = Settings();

        await settings.LoadAsync();

        Assert.Equal(["ready, 2.1.312", "not logged in"], settings.Agents.Select(a => a.Status));
    }

    [Fact]
    public async Task BothPages_ShareTheFile_SoASaveOnOneShowsOnTheOther()
    {
        var config = new ConfigDocument(_engine);
        var repos = new ReposViewModel(_engine, config);
        var settings = new SettingsViewModel(_engine, config);
        await repos.LoadAsync();

        var mode = Field(settings.Policy, "review.mode");
        mode.IsSet = true;
        mode.Value = "approve";
        await settings.SaveCommand.ExecuteAsync(null);
        repos.AddUrl = "https://github.com/acme/api";
        await repos.AddCommand.ExecuteAsync(null);

        Assert.Equal(2, _engine.Saves.Count);
        Assert.Equal("github · claude-code · approve", Assert.Single(repos.Watched).Details);
        Assert.True(JsonNode.DeepEquals(JsonValue.Create("approve"), SettingField.At(config.Settings, "defaults.review.mode")));
    }
}
