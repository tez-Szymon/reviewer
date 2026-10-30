using System.CommandLine;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reviewer.Agents.Cli;
using Reviewer.Cli;
using Reviewer.Core.Agents;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;
using Reviewer.Git;
using Reviewer.Providers.GitHub;

var prUrl = PrUrlArgument();
var dryRun = new Option<bool>("--dry-run") { Description = "Show the review plan without publishing it" };
var full = new Option<bool>("--full") { Description = "Review the whole PR, not only what's new since the last review" };
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

var review = new Command("review", "Review one pull request now") { prUrl, dryRun, full, agent, model, minConfidence };
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
        // With --agent, that agent's own model and timeout apply, and the config hash says so.
        effective = config.For(pr.Repo, parse.GetValue(agent));
    }
    catch (ConfigException e)
    {
        return Fail(e.Message);
    }

    // The overrides go into the hash too: they change what a review keeps, so a head reviewed without them isn't done.
    effective = effective.WithOverrides(parse.GetValue(model), parse.GetValue(minConfidence));

    try
    {
        var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
        await store.FailOrphanedJobsAsync(QueueWorker.IsRunning, ct);
        if ((await store.ListQueueAsync(ct)).FirstOrDefault(j => j.State == JobState.Running && SamePr(j, pr)) is { } running)
        {
            return Fail($"{pr.Repo}#{pr.Number} is being reviewed by job {running.Id}; wait for it or 'reviewer queue cancel {running.Id}'.");
        }

        using var run = await new ReviewFactory(paths, runner, store, home)
            .CreateAsync(pr, effective, parse.GetValue(dryRun), parse.GetValue(full), ct);
        Console.Error.WriteLine($"Reviewing {pr.Repo}#{pr.Number}; this can take several minutes...");
        ReviewPrinter.Print(await run.Runner.RunAsync(run.Request, ct), Console.Out);
        return 0;
    }
    catch (ReviewSetupException e)
    {
        return Fail(e.Message);
    }
    catch (AgentThrottledException e)
    {
        return Fail(e.RetryAt is { } at ? $"{e.Message}\nTry again after {at.ToLocalTime():g}." : e.Message);
    }
    catch (Exception e) when (e is AgentException or GitException or GitHubApiException or HttpRequestException)
    {
        return Fail(e.Message);
    }
    catch (Exception e) when (e is DbException or DbUpdateException)
    {
        return Fail(DatabaseError(paths, e));
    }
});

var historyRepo = new Option<string?>("--repo") { Description = "Only this repo, as owner/name" };
var limit = new Option<int>("--limit") { Description = "How many jobs to list", DefaultValueFactory = _ => 20 };
var history = new Command("history", "List past reviews, newest first") { historyRepo, limit };
history.SetAction(async (parse, ct) =>
{
    if (parse.GetValue(limit) < 1)
    {
        return Fail("--limit must be at least 1.");
    }

    try
    {
        var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
        HistoryPrinter.PrintList(await store.ListJobsAsync(parse.GetValue(historyRepo), parse.GetValue(limit), ct), Console.Out);
        return 0;
    }
    catch (Exception e) when (e is DbException or DbUpdateException)
    {
        return Fail(DatabaseError(paths, e));
    }
});

var jobId = new Argument<string>("job-id") { Description = "A job id from 'reviewer history', or its unique start" };
var showJob = new Command("show", "Show one past review with its findings") { jobId };
showJob.SetAction(async (parse, ct) =>
{
    try
    {
        var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
        if (await store.GetJobAsync(parse.GetValue(jobId)!, ct) is not { } details)
        {
            return Fail($"No job '{parse.GetValue(jobId)}'. See 'reviewer history'.");
        }

        HistoryPrinter.PrintJob(details, Console.Out);
        return 0;
    }
    catch (InvalidOperationException e)
    {
        return Fail(e.Message);
    }
    catch (Exception e) when (e is DbException or DbUpdateException)
    {
        return Fail(DatabaseError(paths, e));
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

var addUrl = PrUrlArgument();
var addDryRun = new Option<bool>("--dry-run") { Description = "Show the review plan without publishing it" };
var addFull = new Option<bool>("--full") { Description = "Review the whole PR, not only what's new since the last review" };
var addAgent = new Option<string?>("--agent") { Description = "Agent to use instead of the configured one" };
var queueAdd = new Command("add", "Queue a review for the engine ('reviewer serve') to run") { addUrl, addDryRun, addFull, addAgent };
queueAdd.SetAction(async (parse, ct) =>
{
    if (!GitHubUrls.TryParsePullRequest(parse.GetValue(addUrl)!, out var pr))
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
        effective = config.For(pr.Repo, parse.GetValue(addAgent));
    }
    catch (ConfigException e)
    {
        return Fail(e.Message);
    }

    if (effective.AgentId != AgentsConfig.ClaudeCodeId)
    {
        return Fail($"Agent '{effective.AgentId}' is not available yet; use claude-code.");
    }

    try
    {
        var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
        var (id, added) = await store.EnqueueAsync(
            new JobRecord
            {
                Id = ReviewContextBuilder.JobId(pr, TimeProvider.System.GetUtcNow()),
                Host = ReviewStore.Host(pr.Repo),
                Repo = pr.Repo.ToString(),
                Number = pr.Number,
                ConfigHash = effective.Hash,
                AgentId = effective.AgentId,
                AppVersion = ReviewStore.AppVersion,
                DryRun = parse.GetValue(addDryRun),
                Full = parse.GetValue(addFull),
            },
            ct);
        Console.WriteLine(added ? $"Queued {id}." : $"{pr.Repo}#{pr.Number} is already queued as {id}.");
        if (!EngineRunning(paths))
        {
            Console.Error.WriteLine("No engine is running; start one with 'reviewer serve'.");
        }

        return 0;
    }
    catch (Exception e) when (e is DbException or DbUpdateException)
    {
        return Fail(DatabaseError(paths, e));
    }
});

var cancelId = new Argument<string>("job-id") { Description = "A job id from 'reviewer queue', or its unique start" };
var queueCancel = new Command("cancel", "Cancel a queued job, or ask the engine to stop a running one") { cancelId };
queueCancel.SetAction((parse, ct) => WithJobAsync(paths, parse.GetValue(cancelId)!, async (store, job) =>
{
    if (job.State is not (JobState.Queued or JobState.Running or JobState.Throttled))
    {
        return Fail($"{job.Id} is {job.State.ToString().ToLowerInvariant()}; there's nothing to cancel.");
    }

    switch (await store.CancelJobAsync(job.Id, ct))
    {
        case JobState.Cancelled:
            Console.WriteLine($"Cancelled {job.Id}.");
            return 0;
        case JobState.Running:
            // Only the engine watches for this; a 'reviewer review' run stops with Ctrl+C in its own terminal.
            Console.WriteLine($"Asked the engine to stop {job.Id}.");
            return 0;
        case var state:
            return Fail($"{job.Id} is {state?.ToString().ToLowerInvariant() ?? "gone"}; there's nothing to cancel.");
    }
}, ct));

var retryId = new Argument<string>("job-id") { Description = "A job id from 'reviewer history', or its unique start" };
var queueRetry = new Command("retry", "Queue a failed, cancelled or throttled job again, to run now") { retryId };
queueRetry.SetAction((parse, ct) => WithJobAsync(paths, parse.GetValue(retryId)!, async (store, job) =>
{
    if (!await store.RetryJobAsync(job.Id, ct))
    {
        return Fail($"{job.Id} is {job.State.ToString().ToLowerInvariant()}; only failed, cancelled or throttled jobs can be retried.");
    }

    Console.WriteLine($"Queued {job.Id} again.");
    if (!EngineRunning(paths))
    {
        Console.Error.WriteLine("No engine is running; start one with 'reviewer serve'.");
    }

    return 0;
}, ct));

var queue = new Command("queue", "List queued, running and throttled reviews") { queueAdd, queueCancel, queueRetry };
queue.SetAction(async (_, ct) =>
{
    try
    {
        var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
        await store.FailOrphanedJobsAsync(QueueWorker.IsRunning, ct);
        var jobs = await store.ListQueueAsync(ct);
        HistoryPrinter.PrintQueue(jobs, TimeProvider.System.GetUtcNow(), Console.Out);
        if (jobs.Any(j => j.State != JobState.Running) && !EngineRunning(paths))
        {
            Console.Error.WriteLine("No engine is running; start one with 'reviewer serve'.");
        }

        return 0;
    }
    catch (Exception e) when (e is DbException or DbUpdateException)
    {
        return Fail(DatabaseError(paths, e));
    }
});

var serve = new Command("serve", "Run the engine: review queued jobs until stopped with Ctrl+C");
serve.SetAction(async (_, ct) =>
{
    if (await LoadConfigAsync(paths, ct) is not { } config)
    {
        return 1;
    }

    FileStream engineLock;
    try
    {
        Directory.CreateDirectory(paths.Root);
        engineLock = new FileStream(paths.EngineLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        return Fail($"Another engine is already running ({paths.EngineLock} is locked).");
    }

    await using (engineLock)
    {
        try
        {
            var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
            using var worker = new QueueWorker(
                store, new QueueLauncher(paths, new ReviewFactory(paths, runner, store, home), config, Log), TimeProvider.System, Log);
            Log($"Engine started on {paths.Root}; Ctrl+C stops it.");
            await worker.RunAsync(ct);
            Log("Engine stopped.");
            return 0;
        }
        catch (Exception e) when (e is DbException or DbUpdateException)
        {
            return Fail(DatabaseError(paths, e));
        }
    }

    static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
});

var root = new RootCommand("Local-first AI code reviewer") { review, queue, serve, history, showJob, status, configCommand };

// Stopping the engine cancels running reviews and queues them again; give that time before the process is ended.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });

static Argument<Uri> PrUrlArgument() => new("pr-url")
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

// Resolves a job id or its unique start, then acts on the job.
static async Task<int> WithJobAsync(ReviewerPaths paths, string idOrPrefix, Func<ReviewStore, JobRecord, Task<int>> action, CancellationToken ct)
{
    try
    {
        var store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, ct);
        if (await store.GetJobAsync(idOrPrefix, ct) is not { } details)
        {
            return Fail($"No job '{idOrPrefix}'. See 'reviewer queue' and 'reviewer history'.");
        }

        return await action(store, details.Job);
    }
    catch (InvalidOperationException e)
    {
        return Fail(e.Message);
    }
    catch (Exception e) when (e is DbException or DbUpdateException)
    {
        return Fail(DatabaseError(paths, e));
    }
}

// The engine holds its lock file exclusively while it runs.
static bool EngineRunning(ReviewerPaths paths)
{
    try
    {
        using var _ = new FileStream(paths.EngineLock, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return false;
    }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
    {
        return false;
    }
    catch (IOException)
    {
        return true;
    }
}

static bool SamePr(JobRecord job, PullRequestRef pr) =>
    string.Equals(job.Host, ReviewStore.Host(pr.Repo), StringComparison.OrdinalIgnoreCase)
    && string.Equals(job.Repo, pr.Repo.ToString(), StringComparison.OrdinalIgnoreCase)
    && job.Number == pr.Number;

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

// A review can fail here after it was published (recording the result), so say where to look.
static string DatabaseError(ReviewerPaths paths, Exception e) =>
    $"Database error in {paths.Database}: {(e.InnerException ?? e).Message}\nIf a review was running, check the PR: it may have been posted already.";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
