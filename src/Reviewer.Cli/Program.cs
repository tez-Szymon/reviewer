using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Reviewer.Agents.Cli;
using Reviewer.Cli;
using Reviewer.Core.Agents;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Git;
using Reviewer.Providers.GitHub;

var prUrl = new Argument<Uri>("pr-url")
{
    Description = "URL of the pull request to review",
    CustomParser = result =>
    {
        var value = result.Tokens.Single().Value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            return uri;
        }

        result.AddError($"'{value}' is not a pull request URL.");
        return null;
    },
};
var dryRun = new Option<bool>("--dry-run") { Description = "Show the review plan without publishing it" };
var agent = new Option<string?>("--agent") { Description = "Agent to use instead of the configured one" };
var model = new Option<string?>("--model") { Description = "Model for the agent, e.g. sonnet or opus; overrides the config" };
var minConfidence = new Option<double?>("--min-confidence")
{
    Description = "Drop findings below this confidence (0-1); overrides the config",
    // The default parser follows the system locale; accept "0.4" (and "0,4") everywhere.
    CustomParser = result =>
    {
        var value = result.Tokens.Single().Value;
        if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 1)
        {
            return number;
        }

        result.AddError($"--min-confidence must be a number between 0 and 1, not '{value}'.");
        return null;
    },
};

var runner = new CliWrapProcessRunner();
var paths = ReviewerPaths.Default;
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var configJson = new JsonSerializerOptions(ConfigLoader.JsonOptions) { WriteIndented = true };

var review = new Command("review", "Review one pull request now") { prUrl, dryRun, agent, model, minConfidence };
review.SetAction(async (parse, ct) =>
{
    if (!GitHubUrls.TryParsePullRequest(parse.GetValue(prUrl)!, out var pr))
    {
        return Fail("Only github.com pull request URLs are supported: https://github.com/<owner>/<repo>/pull/<number>.");
    }

    if (await LoadConfigAsync(paths, ct) is not { } config)
    {
        return 1;
    }

    EffectiveConfig effective;
    try
    {
        effective = config.For(pr.Repo);
    }
    catch (ConfigException e)
    {
        return Fail(e.Message);
    }

    var agentId = parse.GetValue(agent) ?? effective.AgentId;
    if (agentId != AgentsConfig.ClaudeCodeId)
    {
        return Fail($"Agent '{agentId}' is not available yet; use claude-code.");
    }

    if (await GitHubToken.ResolveAsync(runner, effective.Account, ct) is not { } token)
    {
        return Fail(GitHubToken.Missing(effective.Account));
    }

    try
    {
        using var http = GitHubProvider.CreateHttpClient(token);
        var git = await GitRepository.OpenOrInitAsync(paths.Repository(pr.Repo), ct);
        var reviewer = new ReviewRunner(
            new GitHubProvider(http),
            new ReviewContextBuilder(git, paths, TimeProvider.System),
            new ClaudeCodeAgent(new ClaudeCodeOptions(Model: parse.GetValue(model) ?? effective.Agent.Model), runner));
        var policy = effective.Policy.ToReviewPolicy();
        var request = new ReviewRequest(
            pr,
            new ReviewSource(GitHubUrls.CloneUrl(pr.Repo), GitHubUrls.PullHeadRef(pr.Number), new GitCredential("x-access-token", token)),
            policy with
            {
                // The flag can turn a dry run on, never off: a repo set to dry run stays unpublished.
                DryRun = policy.DryRun || parse.GetValue(dryRun),
                MinConfidence = parse.GetValue(minConfidence) ?? policy.MinConfidence,
            },
            await effective.Policy.Instructions.LoadAsync(paths, home, ct),
            effective.Agent.Timeout);

        Console.Error.WriteLine($"Reviewing {pr.Repo}#{pr.Number}; this can take several minutes...");
        ReviewPrinter.Print(await reviewer.RunAsync(request, ct), Console.Out);
        return 0;
    }
    catch (AgentThrottledException e)
    {
        return Fail(e.RetryAt is { } at ? $"{e.Message}\nTry again after {at.ToLocalTime():g}." : e.Message);
    }
    catch (Exception e) when (e is AgentException or GitException or GitHubApiException or HttpRequestException)
    {
        return Fail(e.Message);
    }
});

var status = new Command("status", "Show agent health and the GitHub account in use");
status.SetAction(async (_, ct) =>
{
    var health = await new ClaudeCodeAgent(new ClaudeCodeOptions(), runner).CheckAsync(ct);
    Console.WriteLine(health.IsReady
        ? $"claude-code: ready ({health.Version})"
        : $"claude-code: not ready. {health.Problem}");

    ReviewerConfig? config = null;
    try
    {
        config = await ConfigLoader.LoadAsync(paths.Config, ct);
        Console.WriteLine(File.Exists(paths.Config) ? $"config: {paths.Config}" : "config: built-in defaults (no config.yaml)");
    }
    catch (ConfigException e)
    {
        Console.WriteLine($"config: {paths.Config} is invalid ({e.Errors.Count} errors); run 'reviewer config validate'.");
    }

    var account = (config ?? ReviewerConfig.Default).Accounts[0];
    var github = "github.com: " + GitHubToken.Missing(account);
    if (await GitHubToken.ResolveAsync(runner, account, ct) is { } token)
    {
        using var http = GitHubProvider.CreateHttpClient(token);
        try
        {
            github = $"github.com: signed in as {await new GitHubProvider(http).GetCurrentUserAsync(ct)}";
        }
        catch (Exception e) when (e is GitHubApiException or HttpRequestException)
        {
            github = $"github.com: token rejected. {e.Message}";
        }
    }

    Console.WriteLine(github);
    Console.WriteLine($"data: {paths.Root}");
    return health.IsReady && config is not null && github.Contains("signed in", StringComparison.Ordinal) ? 0 : 1;
});

var validate = new Command("validate", "Check config.yaml and list any problems");
validate.SetAction(async (_, ct) =>
{
    if (await LoadConfigAsync(paths, ct) is not { } config)
    {
        return 1;
    }

    Console.WriteLine(File.Exists(paths.Config)
        ? $"{paths.Config} is valid: {config.Accounts.Count} account(s), {config.Repos.Count} repo(s)."
        : $"No config file at {paths.Config}; the built-in defaults apply.");
    return 0;
});

var repoName = new Argument<string?>("repo") { Description = "owner/name on github.com; omit for the whole config", Arity = ArgumentArity.ZeroOrOne };
var show = new Command("show", "Print the config after defaults and overrides are merged") { repoName };
show.SetAction(async (parse, ct) =>
{
    if (await LoadConfigAsync(paths, ct) is not { } config)
    {
        return 1;
    }

    if (parse.GetValue(repoName) is not { } name)
    {
        Console.WriteLine(JsonSerializer.Serialize(config, configJson));
        return 0;
    }

    if (name.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } repo])
    {
        return Fail($"'{name}' is not owner/name.");
    }

    try
    {
        Console.WriteLine(JsonSerializer.Serialize(config.For(new RepoRef(new Uri("https://github.com"), owner, repo)), configJson));
        return 0;
    }
    catch (ConfigException e)
    {
        return Fail(e.Message);
    }
});

var configCommand = new Command("config", $"Inspect {ConfigLoader.FileName} in the data folder") { validate, show };

var root = new RootCommand("Local-first AI code reviewer") { review, status, configCommand };
return await root.Parse(args).InvokeAsync();

static async Task<ReviewerConfig?> LoadConfigAsync(ReviewerPaths paths, CancellationToken ct)
{
    try
    {
        return await ConfigLoader.LoadAsync(paths.Config, ct);
    }
    catch (ConfigException e)
    {
        Console.Error.WriteLine(e.Message);
        return null;
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
