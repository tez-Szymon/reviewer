using System.CommandLine;
using System.Globalization;
using Reviewer.Agents.Cli;
using Reviewer.Cli;
using Reviewer.Core.Agents;
using Reviewer.Engine;
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
var agent = new Option<string>("--agent") { Description = "Agent to use instead of the configured default", DefaultValueFactory = _ => "claude-code" };
var model = new Option<string?>("--model") { Description = "Model for the agent, e.g. sonnet or opus; defaults to the CLI's own" };
var minConfidence = new Option<double?>("--min-confidence")
{
    Description = "Drop findings below this confidence (0-1); default 0.6",
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

var review = new Command("review", "Review one pull request now") { prUrl, dryRun, agent, model, minConfidence };
review.SetAction(async (parse, ct) =>
{
    if (!GitHubUrls.TryParsePullRequest(parse.GetValue(prUrl)!, out var pr))
    {
        return Fail("Only github.com pull request URLs are supported: https://github.com/<owner>/<repo>/pull/<number>.");
    }

    if (parse.GetValue(agent) != "claude-code")
    {
        return Fail($"Agent '{parse.GetValue(agent)}' is not available yet; use claude-code.");
    }

    if (await GitHubToken.ResolveAsync(runner, ct) is not { } token)
    {
        return Fail(GitHubToken.Missing);
    }

    try
    {
        using var http = GitHubProvider.CreateHttpClient(token);
        var git = await GitRepository.OpenOrInitAsync(paths.Repository(pr.Repo), ct);
        var reviewer = new ReviewRunner(
            new GitHubProvider(http),
            new ReviewContextBuilder(git, paths, TimeProvider.System),
            new ClaudeCodeAgent(new ClaudeCodeOptions(Model: parse.GetValue(model)), runner));
        var request = new ReviewRequest(
            pr,
            new ReviewSource(GitHubUrls.CloneUrl(pr.Repo), GitHubUrls.PullHeadRef(pr.Number), new GitCredential("x-access-token", token)),
            ReviewDefaults.Policy with
            {
                DryRun = parse.GetValue(dryRun),
                MinConfidence = parse.GetValue(minConfidence) ?? ReviewDefaults.Policy.MinConfidence,
            },
            await ReviewDefaults.InstructionsAsync(paths, ct),
            ReviewDefaults.Timeout);

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

    var github = "github.com: " + GitHubToken.Missing;
    if (await GitHubToken.ResolveAsync(runner, ct) is { } token)
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
    return health.IsReady && github.Contains("signed in", StringComparison.Ordinal) ? 0 : 1;
});

var root = new RootCommand("Local-first AI code reviewer") { review, status };
return await root.Parse(args).InvokeAsync();

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
