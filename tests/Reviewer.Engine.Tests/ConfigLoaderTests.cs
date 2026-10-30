using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Instructions;

namespace Reviewer.Engine.Tests;

public class ConfigLoaderTests
{
    private static readonly RepoRef Api = new(new Uri("https://github.com"), "acme", "api");

    private static string Error(string yaml) => Assert.Single(Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Errors);

    [Fact]
    public void Parse_Empty_UsesBuiltInDefaults()
    {
        var config = ConfigLoader.Parse("");

        var account = Assert.Single(config.Accounts);
        Assert.Equal(new Uri("https://github.com"), account.Host);
        Assert.Null(account.Token);
        Assert.Equal(AgentsConfig.ClaudeCodeId, config.Agents.Default);
        Assert.Equal(15, config.Agents.ClaudeCode.TimeoutMinutes);

        var policy = config.Defaults.ToReviewPolicy();
        Assert.Equal(ReviewMode.Comment, policy.Mode);
        Assert.False(policy.DryRun);
        Assert.Equal(0.6, policy.MinConfidence);
        Assert.Equal(20, policy.MaxInlineComments);
        Assert.Equal(["**/*.lock", "**/dist/**", "**/*.min.js"], policy.IgnorePaths);
        Assert.Equal(new BlockingRule(Severity.Blocker), policy.BlockingRules[1]);
        Assert.Equal([Category.Security, Category.Correctness], policy.BlockingRules[0].Categories!);
        Assert.Equal([InitialTrigger.AssignedToMe, InitialTrigger.ReviewRequested], config.Defaults.Triggers.Initial);
        Assert.Equal(RepoAgentFileMode.Context, config.Defaults.Instructions.RepoAgentFiles);
        Assert.Empty(config.Repos);
    }

    [Fact]
    public async Task Load_MissingFile_UsesBuiltInDefaults()
    {
        var config = await ConfigLoader.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.yaml"), CancellationToken.None);

        Assert.Equal(ReviewerConfig.Default.For(Api).Hash, config.For(Api).Hash);
    }

    [Fact]
    public void Parse_UserDefaults_ChangeOnlyTheKeysTheySet()
    {
        var config = ConfigLoader.Parse("""
            defaults:
              review:
                mode: gate
                ignorePaths: ["docs/**"]
            """);

        Assert.Equal(ReviewMode.Gate, config.Defaults.Review.Mode);
        Assert.Equal(0.6, config.Defaults.Review.MinConfidence);
        Assert.Equal(["docs/**"], config.Defaults.Review.IgnorePaths); // Lists replace, they don't append.
        Assert.Equal(2, config.Defaults.Review.BlockingRules.Count);
    }

    [Fact]
    public void For_RepoOverrides_MergeOntoDefaults()
    {
        var config = ConfigLoader.Parse("""
            defaults:
              review: { minConfidence: 0.5 }
            repos:
              - repo: acme/api
                agent: claude-code
                overrides:
                  review: { mode: approve }
                  instructions: { repoSkills: ["~/skills/payments/*.md"] }
            """);

        var effective = config.For(Api);

        Assert.Equal(ReviewMode.Approve, effective.Policy.Review.Mode);
        Assert.Equal(0.5, effective.Policy.Review.MinConfidence);
        Assert.Equal(["skills/*.md"], effective.Policy.Instructions.Skills);
        Assert.Equal(["~/skills/payments/*.md"], effective.Policy.Instructions.RepoSkills);
        Assert.Equal(ReviewMode.Comment, config.For(Api with { Name = "web" }).Policy.Review.Mode);
    }

    [Fact]
    public void For_MatchesRepoCaseInsensitively()
    {
        var config = ConfigLoader.Parse("repos: [{ repo: Acme/API, overrides: { review: { mode: gate } } }]");

        Assert.Equal(ReviewMode.Gate, config.For(Api).Policy.Review.Mode);
    }

    [Fact]
    public void For_UnknownHost_Throws()
    {
        var error = Assert.Throws<ConfigException>(() => ReviewerConfig.Default.For(Api with { Host = new Uri("https://gitlab.com") }));

        Assert.Contains("gitlab.com", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_ChangesWithPolicyAndAgent_ButNotTriggers()
    {
        string HashOf(string yaml) => ConfigLoader.Parse(yaml).For(Api).Hash;
        var baseline = HashOf("");

        Assert.Equal(baseline, HashOf(""));
        Assert.Equal(baseline, HashOf("defaults: { triggers: { skipDrafts: false } }"));
        Assert.NotEqual(baseline, HashOf("defaults: { review: { minConfidence: 0.7 } }"));
        Assert.NotEqual(baseline, HashOf("agents: { claude-code: { model: opus } }"));
        Assert.Matches("^[0-9a-f]{16}$", baseline);
    }

    [Fact]
    public void Parse_QuotedScalars_StayText()
    {
        var config = ConfigLoader.Parse("""
            defaults:
              triggers: { initial: [has-label], labels: ["true", "1.0", ai-review] }
            """);

        Assert.Equal(["true", "1.0", "ai-review"], config.Defaults.Triggers.Labels);
    }

    [Fact]
    public void Parse_UnknownKey_NamesKeyAndLine()
    {
        Assert.Equal("config.yaml:3: defaults.review.mod: unknown key.", Error("""
            defaults:
              review:
                mod: gate
            """));
    }

    [Fact]
    public void Parse_BadEnum_ListsAllowedValues()
    {
        Assert.Equal(
            "config.yaml:1: defaults.review.mode: must be one of: comment, approve, gate, gate-merge.",
            Error("defaults: { review: { mode: block } }"));
    }

    [Fact]
    public void Parse_WrongType_SaysWhatWasExpected()
    {
        Assert.Equal("config.yaml:2: defaults.review.maxInlineComments: expected a whole number.", Error("""
            defaults:
              review: { maxInlineComments: lots }
            """));
    }

    [Fact]
    public void Parse_RepoOverrideError_PointsAtTheOverride()
    {
        Assert.Equal("config.yaml:4: repos[0].overrides.review.dryrun: unknown key.", Error("""
            repos:
              - repo: acme/api
                overrides:
                  review: { dryrun: true }
            """));
    }

    [Fact]
    public void Parse_BadDefault_IsReportedOnce()
    {
        Assert.Equal("config.yaml:1: defaults.review.minConfidence: must be between 0 and 1.", Error("""
            defaults: { review: { minConfidence: 6 } }
            repos:
              - { repo: acme/api, overrides: { review: { mode: gate } } }
            """));
    }

    [Fact]
    public void Parse_LiteralToken_IsRejectedWithoutEchoingIt()
    {
        var error = Error("accounts: [{ id: gh, provider: github, token: ghp_secret123 }]");

        Assert.Contains("accounts[0].token: must be a reference such as env:GITHUB_TOKEN", error, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_secret123", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accounts: [{ id: gl, provider: gitlab, host: https://gitlab.com }]", "GitLab support arrives in M3")]
    [InlineData("accounts: [{ id: ghe, host: https://git.acme.com }]", "only https://github.com is supported")]
    [InlineData("accounts: [{ id: a }, { id: a }]", "accounts[1].id: 'a' is already used")]
    [InlineData("agents: { default: gpt }", "agents.default: must be one of: claude-code, codex.")]
    [InlineData("defaults: { review: { mode: gate-merge } }", "gate-merge arrives in M3")]
    [InlineData("defaults: { triggers: { initial: [has-label] } }", "has-label needs at least one label")]
    [InlineData("repos: [{ repo: api }]", "repos[0].repo: must be owner/name.")]
    [InlineData("repos: [{ repo: acme/api, account: work }]", "repos[0].account: no account with id 'work'.")]
    [InlineData("repos: [{ repo: acme/api }, { repo: ACME/api }]", "repos[1].repo: ACME/api is listed twice.")]
    [InlineData("accounts: [{ id: a }, { id: b }]\nrepos: [{ repo: acme/api }]", "repos[0]: set account")]
    [InlineData("defaults: { review: { mode: gate } }\ndefaults: {}", "config.yaml:2: Duplicate key defaults")]
    [InlineData("defaults: { review: [", "config.yaml:2: While parsing a node")]
    [InlineData("- a\n- b", "the top level must be a map")]
    [InlineData("defaults: { review: { blockingRules: [{ categories: [security] }] } }", "missing")]
    [InlineData("defaults: { review: { ignorePaths: ~ } }", "defaults.review.ignorePaths: a value is required.")]
    public void Parse_InvalidConfig_ExplainsTheProblem(string yaml, string expected)
    {
        Assert.Contains(expected, Error(yaml), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsEveryValidationError()
    {
        var errors = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("""
            agents: { claude-code: { maxConcurrency: 0, timeoutMinutes: 0 } }
            """)).Errors;

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task Instructions_ResolveGlobsFromHomeDataFolderAndAbsolutePaths()
    {
        var temp = Directory.CreateTempSubdirectory("reviewer-config-");
        try
        {
            var root = Path.Combine(temp.FullName, "data");
            var home = Path.Combine(temp.FullName, "home");
            var elsewhere = Path.Combine(temp.FullName, "elsewhere");
            foreach (var (dir, name) in new[] { (Path.Combine(root, "skills"), "a"), (Path.Combine(home, "skills"), "b"), (elsewhere, "c") })
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, name + ".md"), "Look for " + name, CancellationToken.None);
            }

            var instructions = ConfigLoader.Parse($$"""
                defaults:
                  instructions:
                    skills: ["skills/*.md", "~/skills/*.md"]
                    repoSkills: ["{{elsewhere.Replace('\\', '/')}}/*.md"]
                    repoAgentFiles: ignore
                """).Defaults.Instructions;

            var settings = await instructions.LoadAsync(new ReviewerPaths(root), home, CancellationToken.None);

            Assert.Equal(["a", "b"], settings.UserSkills.Select(s => s.Name));
            Assert.Equal("c", Assert.Single(settings.RepoSkills).Name);
            Assert.Equal(RepoAgentFileMode.Ignore, settings.AgentFileMode);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
