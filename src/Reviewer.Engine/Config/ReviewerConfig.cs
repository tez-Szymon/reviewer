using System.Text.Json.Serialization;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Instructions;

namespace Reviewer.Engine.Config;

/// <summary>
/// <c>config.yaml</c> after loading: built-in defaults, the user's file on top, and each repo's overrides merged into
/// its own policy. Produced only by <see cref="ConfigLoader"/>, which has validated it.
/// </summary>
/// <param name="Accounts">Never empty: without accounts in the file there is one implicit github.com account.</param>
/// <param name="Defaults">The global policy, used for repos without an entry.</param>
public sealed record ReviewerConfig(
    IReadOnlyList<AccountConfig> Accounts,
    AgentsConfig Agents,
    PolicyConfig Defaults,
    IReadOnlyList<RepoConfig> Repos)
{
    /// <summary>The built-in defaults alone, as when there is no config file.</summary>
    public static ReviewerConfig Default { get; } = ConfigLoader.Parse("");

    /// <summary>
    /// The settings a review of <paramref name="repo"/> runs with: its entry when it has one, otherwise the defaults
    /// and the account for its host.
    /// </summary>
    /// <param name="agentOverride">An agent to use instead of the configured one, e.g. from <c>--agent</c>; its own settings apply.</param>
    /// <exception cref="ConfigException">No account is configured for the repo's host, or the override is no known agent.</exception>
    public EffectiveConfig For(RepoRef repo, string? agentOverride = null)
    {
        if (agentOverride is not null && !AgentsConfig.Known.Contains(agentOverride))
        {
            throw new ConfigException([$"Unknown agent '{agentOverride}'; use one of: {string.Join(", ", AgentsConfig.Known)}."]);
        }

        var entry = Repos.FirstOrDefault(r =>
            string.Equals(r.Repo, $"{repo.Owner}/{repo.Name}", StringComparison.OrdinalIgnoreCase)
            && SameHost(Account(r.Account).Host, repo.Host));
        var account = entry is not null
            ? Account(entry.Account)
            : Accounts.FirstOrDefault(a => SameHost(a.Host, repo.Host))
              ?? throw new ConfigException([$"No account in the config for {repo.Host.Host}."]);
        var agentId = agentOverride ?? entry?.Agent ?? Agents.Default;
        var agent = Agents.Get(agentId);
        var policy = entry?.Policy ?? Defaults;
        return new EffectiveConfig(account, agentId, agent, policy, ConfigLoader.Hash(agentId, agent, policy));
    }

    // Validation guarantees that a named account exists and that an unnamed one is unambiguous.
    private AccountConfig Account(string? id) => id is null ? Accounts[0] : Accounts.First(a => a.Id == id);

    /// <summary>Same scheme, host and port: what decides which account a PR URL belongs to.</summary>
    internal static bool SameHost(Uri a, Uri b) => Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}

/// <param name="Token">
/// A reference to the token, never the token itself: <c>env:NAME</c> reads an environment variable. Null falls back to
/// <c>GITHUB_TOKEN</c>, <c>GH_TOKEN</c> and the GitHub CLI's login.
/// </param>
public sealed record AccountConfig(string Id, string Provider, Uri Host, string? Token);

public sealed record AgentsConfig(
    string Default,
    [property: JsonPropertyName(AgentsConfig.ClaudeCodeId)] AgentSettings ClaudeCode,
    [property: JsonPropertyName(AgentsConfig.CodexId)] AgentSettings Codex)
{
    public const string ClaudeCodeId = "claude-code";
    public const string CodexId = "codex";

    public static IReadOnlyList<string> Known { get; } = [ClaudeCodeId, CodexId];

    public AgentSettings Get(string id) => id switch
    {
        ClaudeCodeId => ClaudeCode,
        CodexId => Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown agent."),
    };
}

/// <param name="Model">Model alias or name for the CLI; null uses the CLI's own default.</param>
public sealed record AgentSettings(int MaxConcurrency, int TimeoutMinutes, string? Model = null)
{
    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromMinutes(TimeoutMinutes);
}

/// <param name="Account">Id of the account that watches and reviews the repo.</param>
/// <param name="Agent">Overrides the default agent for this repo.</param>
/// <param name="Policy">The defaults with this repo's overrides merged in.</param>
public sealed record RepoConfig(string Repo, string? Account, string? Agent, PolicyConfig Policy);

/// <summary>The policy for one repo, or the global defaults. Repo overrides change only the keys they set.</summary>
public sealed record PolicyConfig(
    TriggerConfig Triggers,
    ReviewConfig Review,
    MergeConfig Merge,
    InstructionsConfig Instructions)
{
    public ReviewPolicy ToReviewPolicy() => new(
        Review.Mode,
        Review.DryRun,
        Review.MinConfidence,
        Review.BlockingRules,
        Review.IgnorePaths,
        Review.MaxInlineComments,
        Review.RequestChangesFallback);
}

/// <summary>Which PRs the scheduler reviews and when it reviews them again.</summary>
/// <param name="Initial">What makes a PR due for its first review; any one is enough.</param>
/// <param name="Labels">For <see cref="InitialTrigger.HasLabel"/>.</param>
/// <param name="Authors">For <see cref="InitialTrigger.AuthoredBy"/>.</param>
/// <param name="TargetBranches">Only PRs into these branches; empty means any.</param>
/// <param name="Paths">Only PRs touching a file matching one of these globs; empty means any.</param>
public sealed record TriggerConfig(
    IReadOnlyList<InitialTrigger> Initial,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Authors,
    ReReviewConfig ReReview,
    bool SkipDrafts,
    IReadOnlyList<string> TargetBranches,
    IReadOnlyList<string> Paths,
    int MaxDiffLines);

public enum InitialTrigger
{
    All,
    AssignedToMe,
    ReviewRequested,
    HasLabel,
    AuthoredBy,
}

/// <param name="DebounceMinutes">Wait this long after the last push, so a burst of pushes produces one review.</param>
public sealed record ReReviewConfig(bool OnNewCommits, int DebounceMinutes, bool OnReassignment, bool OnReviewRequested);

/// <param name="IgnorePaths">Findings in files matching these globs are dropped.</param>
public sealed record ReviewConfig(
    ReviewMode Mode,
    bool DryRun,
    double MinConfidence,
    IReadOnlyList<BlockingRule> BlockingRules,
    int MaxInlineComments,
    IReadOnlyList<string> IgnorePaths,
    RequestChangesFallback RequestChangesFallback);

public sealed record MergeConfig(MergeMethod Method, bool RequireGreenCi);

/// <param name="Skills">User skill globs (layer 3). <c>~</c> is the home directory; relative paths start at the reviewer's data folder.</param>
/// <param name="RepoSkills">Skill globs for this repo (layer 4), resolved the same way.</param>
public sealed record InstructionsConfig(
    bool UseDefaultGuide,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> RepoSkills,
    RepoAgentFileMode RepoAgentFiles)
{
    /// <summary>Reads the skill files the globs match; missing directories match nothing.</summary>
    /// <param name="home">The user's home directory, for a leading <c>~</c>.</param>
    public async Task<InstructionSettings> LoadAsync(ReviewerPaths paths, string home, CancellationToken ct)
    {
        IEnumerable<string> Resolve(IReadOnlyList<string> globs) => globs.Select(glob =>
            glob == "~" || glob.StartsWith("~/", StringComparison.Ordinal) || Path.IsPathRooted(glob) ? glob : Path.Combine(paths.Root, glob));

        var user = await SkillLoader.LoadAsync(Resolve(Skills), home, ct).ConfigureAwait(false);
        var repo = await SkillLoader.LoadAsync(Resolve(RepoSkills), home, ct).ConfigureAwait(false);
        return new InstructionSettings(UseDefaultGuide, user, repo, RepoAgentFiles);
    }
}

/// <summary>Everything a review of one repo runs with.</summary>
/// <param name="Hash">
/// Identifies the settings that shape a review's result: agent and policy, but not triggers. Part of the key that
/// says whether a PR head has already been reviewed.
/// </param>
public sealed record EffectiveConfig(AccountConfig Account, string AgentId, AgentSettings Agent, PolicyConfig Policy, string Hash);

/// <summary>The config file is invalid. Each error names the key and, when known, the line.</summary>
public sealed class ConfigException(IReadOnlyList<string> errors) : Exception(string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
