using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reviewer.Core.Agents;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;
using Reviewer.Providers.GitHub;

namespace Reviewer.Api;

/// <summary>What the API serves from: the engine's store and folders, its agents, and a way to wake its queue worker.</summary>
/// <param name="Wake">Makes the queue worker look at the queue now, e.g. <see cref="QueueWorker.Wake"/>.</param>
/// <param name="Providers">
/// The provider for an account, for browsing its repos; throws <see cref="InvalidOperationException"/> when the account
/// can't connect, e.g. without a token. Null serves no account endpoints that need one.
/// </param>
public sealed record EngineServices(
    ReviewerPaths Paths,
    ReviewStore Store,
    IReadOnlyList<IReviewAgent> Agents,
    Action Wake,
    TimeProvider Time,
    DateTimeOffset StartedAt,
    Func<AccountConfig, CancellationToken, Task<IRepositoryProvider>>? Providers = null)
{
    /// <summary>Stops the engine as Ctrl+C does; null when it can only be stopped from its terminal.</summary>
    public Action? Stop { get; init; }
}

/// <summary>
/// The engine's local HTTP API: a minimal API on a Unix domain socket in the data folder, never on TCP, so other users
/// and browser pages can't reach it. The CLI and the desktop app are its clients.
/// </summary>
/// <remarks>
/// <para>
/// The API reads and writes the same database as the CLI, so it shows jobs the CLI added too. Config is read from the
/// file on each request, as the engine reads it before each job.
/// </para>
/// <para>
/// <c>GET /events</c> streams a <c>job</c> event (a <see cref="JobDto"/>) each time a job changes, by any process,
/// with the job's <see cref="JobDto.Version"/> as the event id. A client opens it first, then loads the lists, and
/// keeps the higher version of each job; reconnecting with <c>Last-Event-ID</c> resumes without gaps.
/// </para>
/// </remarks>
public static class LocalApi
{
    /// <summary>How often <c>/events</c> looks for changes; other processes' writes are seen only by polling.</summary>
    public static readonly TimeSpan EventPollInterval = TimeSpan.FromSeconds(1);

    private const int EventBatch = 100;

    /// <summary>
    /// Starts the API on <paramref name="socketPath"/>, replacing a socket file a stopped engine left behind; only one
    /// engine runs at a time, so it can't be in use. Only the current user can connect.
    /// </summary>
    /// <param name="log">Gets the API's warnings and errors, such as a request that failed.</param>
    public static async Task<WebApplication> StartAsync(
        EngineServices engine, string socketPath, Action<string>? log, CancellationToken ct, TimeSpan? eventPollInterval = null)
    {
        File.Delete(socketPath);
        var app = Build(engine, socketPath, log, eventPollInterval ?? EventPollInterval);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return app;
    }

    private static WebApplication Build(EngineServices engine, string socketPath, Action<string>? log, TimeSpan eventPollInterval)
    {
        // The slim builder, with the content root away from the working directory so no appsettings.json is picked up.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        if (log is not null)
        {
            builder.Logging.AddProvider(new LogForwarder(log));
        }

        // The engine owns Ctrl+C and stops the API itself; the default console lifetime would take the signal from it.
        builder.Services.AddSingleton<IHostLifetime, EngineLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(socketPath));
        builder.Services.ConfigureHttpJsonOptions(json => ApiJson.Configure(json.SerializerOptions));
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();

        var app = builder.Build();
        app.MapOpenApi();
        Map(app, engine, eventPollInterval);
        return app;
    }

    private static void Map(IEndpointRouteBuilder api, EngineServices engine, TimeSpan eventPollInterval)
    {
        var store = engine.Store;

        api.MapGet("/status", () => new EngineStatus(ReviewStore.AppVersion, Environment.ProcessId, engine.StartedAt, engine.Paths.Root))
            .WithSummary("The engine process");

        api.MapPost("/shutdown", Results<Accepted, ProblemHttpResult> () =>
        {
            if (engine.Stop is null)
            {
                return TypedResults.Problem("This engine can only be stopped from its terminal.", statusCode: StatusCodes.Status501NotImplemented);
            }

            engine.Stop();
            return TypedResults.Accepted((string?)null);
        }).WithSummary("Stop the engine as Ctrl+C does: a running review is cancelled and requeued, then the API closes");

        api.MapGet("/agents", async (CancellationToken ct) =>
        {
            var checks = engine.Agents.Select(async agent =>
            {
                var health = await agent.CheckAsync(ct).ConfigureAwait(false);
                return new AgentStatus(agent.Id, health.IsReady, health.IsInstalled, health.IsLoggedIn, health.Version, health.Problem);
            });
            return await Task.WhenAll(checks).ConfigureAwait(false);
        }).WithSummary("Whether each agent is installed, logged in and ready");

        api.MapGet("/accounts", async Task<Results<Ok<List<AccountDto>>, ProblemHttpResult>> (CancellationToken ct) =>
        {
            try
            {
                var config = await ConfigLoader.LoadAsync(engine.Paths.Config, ct).ConfigureAwait(false);
                return TypedResults.Ok(config.Accounts.Select(a => new AccountDto(a.Id, a.Provider, a.Host, a.Token)).ToList());
            }
            catch (ConfigException e)
            {
                return ConfigProblem(e, StatusCodes.Status422UnprocessableEntity);
            }
        }).WithSummary("The accounts in the config; without any, the implicit github.com one");

        api.MapGet("/accounts/{id}/status", (string id, CancellationToken ct) => WithProviderAsync(engine, id, async (account, provider) =>
        {
            try
            {
                return new AccountStatus(account.Id, true, await provider.GetCurrentUserAsync(ct).ConfigureAwait(false), null);
            }
            catch (ProviderApiException e)
            {
                return new AccountStatus(account.Id, false, null, e.Message);
            }
        }, ct, connectProblem: (account, message) => new AccountStatus(account.Id, false, null, message)))
            .WithSummary("Whether the account's token works, and whose it is");

        api.MapGet("/accounts/{id}/repos", (string id, string? owner, CancellationToken ct) => WithProviderAsync(engine, id, async (account, provider) =>
        {
            var config = await ConfigLoader.LoadAsync(engine.Paths.Config, ct).ConfigureAwait(false);
            var watched = config.Repos
                .Where(r => (r.Account ?? config.Accounts[0].Id) == account.Id)
                .Select(r => r.Repo)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var repos = new List<RepoChoice>();
            await foreach (var repo in provider.ListAccessibleReposAsync(owner, ct).ConfigureAwait(false))
            {
                repos.Add(new RepoChoice(repo.ToString(), watched.Contains(repo.ToString())));
            }

            return repos;
        }, ct)).WithSummary("Repos the account can see, all or one owner's (a user, an organization, or a GitLab group with its subgroups), marked when watched");

        api.MapGet("/queue", async (CancellationToken ct) =>
                (await store.ListQueueAsync(ct).ConfigureAwait(false)).Select(JobDto.From).ToList())
            .WithSummary("Queued, running and throttled jobs, oldest first");

        api.MapGet("/jobs", async Task<Results<Ok<List<JobSummaryDto>>, ProblemHttpResult>> (
            string? repo, int? number, int? limit, CancellationToken ct) =>
        {
            if (limit is < 1 or > 500)
            {
                return TypedResults.Problem("limit must be between 1 and 500.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (number is not null && repo is null)
            {
                return TypedResults.Problem("number needs repo.", statusCode: StatusCodes.Status400BadRequest);
            }

            var jobs = await store.ListJobsAsync(repo, limit ?? 20, ct, number).ConfigureAwait(false);
            return TypedResults.Ok(jobs.Select(JobSummaryDto.From).ToList());
        }).WithSummary("Past and current jobs, newest first; repo is owner/name");

        api.MapGet("/jobs/{id}", async Task<Results<Ok<JobDetailsDto>, NotFound, ProblemHttpResult>> (string id, CancellationToken ct) =>
            await FindJobAsync(store, id, ct).ConfigureAwait(false) switch
            {
                (null, null) => TypedResults.NotFound(),
                (_, { } problem) => problem,
                ({ } details, _) => TypedResults.Ok(JobDetailsDto.From(details, await ReadReasonsAsync(details.Job.AttemptFolder, ct).ConfigureAwait(false))),
            }).WithSummary("One job with its findings, by id or its unique start");

        api.MapPost("/jobs", async Task<Results<Created<QueueResult>, Ok<QueueResult>, ProblemHttpResult>> (
            QueueRequest request, CancellationToken ct) =>
        {
            EnqueueResult result;
            try
            {
                // The accounts say which hosts are GitLab, so the URL is read with the config.
                var config = await ConfigLoader.LoadAsync(engine.Paths.Config, ct).ConfigureAwait(false);
                if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || !PullRequestUrls.TryParse(url, config, out var pr))
                {
                    return TypedResults.Problem(PullRequestUrls.Expected, statusCode: StatusCodes.Status400BadRequest);
                }

                result = await ManualReview.QueueAsync(store, config, pr, request.Agent, request.DryRun, request.Full, engine.Time, ct)
                    .ConfigureAwait(false);
            }
            catch (ConfigException e)
            {
                return ConfigProblem(e, StatusCodes.Status400BadRequest);
            }

            engine.Wake();
            var body = new QueueResult(result.JobId, result.Added);
            return result.Added ? TypedResults.Created($"/jobs/{result.JobId}", body) : TypedResults.Ok(body);
        }).WithSummary("Queue a review of a PR, or merge the request into one already queued");

        api.MapPost("/jobs/{id}/cancel", async Task<Results<Ok<CancelResult>, NotFound, ProblemHttpResult>> (string id, CancellationToken ct) =>
        {
            var (details, problem) = await FindJobAsync(store, id, ct).ConfigureAwait(false);
            if (details is null)
            {
                return problem is null ? TypedResults.NotFound() : problem;
            }

            var job = details.Job;
            var state = job.State is JobState.Queued or JobState.Running or JobState.Throttled
                ? await store.CancelJobAsync(job.Id, ct).ConfigureAwait(false)
                : job.State;
            if (state is not (JobState.Cancelled or JobState.Running) || job.State == JobState.Cancelled)
            {
                return Conflict($"{job.Id} is {Name(state ?? job.State)}; there's nothing to cancel.");
            }

            engine.Wake(); // A running job is stopped by the worker's next pass.
            return TypedResults.Ok(new CancelResult(job.Id, state.Value));
        }).WithSummary("Cancel a waiting job, or ask the engine to stop a running one");

        api.MapPost("/jobs/{id}/retry", async Task<Results<Ok<JobDto>, NotFound, ProblemHttpResult>> (string id, CancellationToken ct) =>
        {
            var (details, problem) = await FindJobAsync(store, id, ct).ConfigureAwait(false);
            if (details is null)
            {
                return problem is null ? TypedResults.NotFound() : problem;
            }

            if (!await store.RetryJobAsync(details.Job.Id, ct).ConfigureAwait(false))
            {
                return Conflict($"{details.Job.Id} is {Name(details.Job.State)}; only failed, cancelled or throttled jobs can be retried.");
            }

            engine.Wake();
            var retried = await store.GetJobAsync(details.Job.Id, ct).ConfigureAwait(false);
            return TypedResults.Ok(JobDto.From(retried!.Job));
        }).WithSummary("Queue a failed, cancelled or throttled job again, to run now");

        api.MapGet("/pulls", async (string? repo, CancellationToken ct) =>
            (await store.ListWatchedAsync(repo, ct).ConfigureAwait(false)).Select(WatchedPullRequestDto.From).ToList())
            .WithSummary("Open PRs in watched repos as reviewer serve last polled them, with each one's last review and newest job");

        api.MapGet("/pulls/{owner}/{repo}/{number:int}", async Task<Results<Ok<PullRequestDto>, ProblemHttpResult>> (
            string owner, string repo, int number, string? host, CancellationToken ct) =>
        {
            if (!TryHost(host, out var hostUri))
            {
                return TypedResults.Problem($"'{host}' is not a host URL.", statusCode: StatusCodes.Status400BadRequest);
            }

            var pr = new PullRequestRef(new RepoRef(hostUri, Uri.UnescapeDataString(owner), repo), number);
            var record = await store.GetPullRequestAsync(pr, ct).ConfigureAwait(false);
            var open = await store.GetOpenFindingsAsync(pr, ct).ConfigureAwait(false);
            var jobs = await store.ListJobsAsync(pr.Repo.ToString(), 50, ct, number).ConfigureAwait(false);
            return TypedResults.Ok(new PullRequestDto(
                ReviewStore.Host(pr.Repo),
                pr.Repo.ToString(),
                number,
                record is null ? null : new LastReviewDto(record.LastReviewedHead, record.LastReviewedBase, record.LastReviewedAt, record.LastJobId),
                [.. open.Select(f => new OpenFindingDto(f.Finding, f.Fingerprint, f.HeadSha, f.Blocking))],
                [.. jobs.Where(j => string.Equals(j.Job.Host, ReviewStore.Host(pr.Repo), StringComparison.OrdinalIgnoreCase))
                    .Select(JobSummaryDto.From)]));
        }).WithSummary("A PR's last published review, its open findings and its jobs; host defaults to https://github.com, and a GitLab subgroup path is one escaped owner segment");

        api.MapGet("/config", async Task<Results<JsonHttpResult<ReviewerConfig>, ProblemHttpResult>> (CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Json(await ConfigLoader.LoadAsync(engine.Paths.Config, ct).ConfigureAwait(false), ConfigLoader.JsonOptions);
            }
            catch (ConfigException e)
            {
                return ConfigProblem(e, StatusCodes.Status422UnprocessableEntity);
            }
        }).WithSummary("config.yaml after the built-in defaults are merged in");

        api.MapGet("/config/repos/{owner}/{repo}", async Task<Results<JsonHttpResult<EffectiveConfig>, ProblemHttpResult>> (
            string owner, string repo, string? host, CancellationToken ct) =>
        {
            if (!TryHost(host, out var hostUri))
            {
                return TypedResults.Problem($"'{host}' is not a host URL.", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var config = await ConfigLoader.LoadAsync(engine.Paths.Config, ct).ConfigureAwait(false);
                return TypedResults.Json(config.For(new RepoRef(hostUri, Uri.UnescapeDataString(owner), repo)), ConfigLoader.JsonOptions);
            }
            catch (ConfigException e)
            {
                return ConfigProblem(e, StatusCodes.Status422UnprocessableEntity);
            }
        }).WithSummary("The merged settings a review of the repo uses");

        api.MapPost("/config/validate", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var yaml = await reader.ReadToEndAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
            try
            {
                ConfigLoader.Parse(yaml);
                return new ConfigValidation(true, []);
            }
            catch (ConfigException e)
            {
                return new ConfigValidation(false, e.Errors);
            }
        }).Accepts<string>("application/yaml", "text/plain").WithSummary("Check a config.yaml without saving it");

        api.MapGet("/config/file", (CancellationToken ct) => ConfigFile.ReadAsync(engine.Paths.Config, ct))
            .WithSummary("config.yaml as written, with the hash a save must name; empty when there's no file");

        api.MapPut("/config/file", (ConfigSave save, CancellationToken ct) =>
            SaveConfigAsync(() => ConfigFile.SaveAsync(engine.Paths.Config, save.Yaml, save.BaseHash, ct)))
            .WithSummary("Replace config.yaml, if it still has the base hash and the new one is valid");

        api.MapPatch("/config", (ConfigEdits request, CancellationToken ct) =>
            SaveConfigAsync(() => ConfigFile.EditAsync(engine.Paths.Config, request.Edits, request.BaseHash, ct)))
            .WithSummary("Set, remove or add values at key paths such as repos[0].overrides.review.mode, changing only those spans of config.yaml, so its comments and layout stay");

        api.MapGet("/events", async (HttpContext context, IHostApplicationLifetime lifetime, long? after, CancellationToken ct) =>
        {
            // Last-Event-ID is what an EventSource sends when it reconnects; after lets a client start from a version it has.
            // "From now on" is fixed before the response starts, so a client that loads its lists once the stream is open
            // misses no change made in between.
            var from = long.TryParse(context.Request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                ? last
                : after ?? await store.GetLatestVersionAsync(ct).ConfigureAwait(false);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            context.Response.RegisterForDispose(stop);
            // The response is sent with its first event otherwise; flushing it at once tells the client the stream is
            // open, which is when it loads its lists.
            return TypedResults.ServerSentEvents(JobEventsAsync(store, from, eventPollInterval, () => context.Response.Body.FlushAsync(stop.Token), stop.Token));
        }).WithSummary("Server-sent 'job' events, one per change to a job, by any process");
    }

    /// <summary>Each change to a job after <paramref name="after"/>, until cancelled.</summary>
    /// <param name="started">Called when streaming starts, before any change is looked for: sends the response headers.</param>
    internal static async IAsyncEnumerable<SseItem<JobDto>> JobEventsAsync(
        ReviewStore store, long after, TimeSpan pollInterval, Func<Task> started, [EnumeratorCancellation] CancellationToken ct)
    {
        var cursor = after;
        IReadOnlyList<JobRecord> changes;
        try
        {
            await started().ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // The client went away.
            yield break;
        }

        while (true)
        {
            try
            {
                changes = await store.ListJobChangesAsync(cursor, EventBatch, ct).ConfigureAwait(false);
                if (changes.Count == 0)
                {
                    await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            foreach (var job in changes)
            {
                cursor = job.Version;
                yield return new SseItem<JobDto>(JobDto.From(job), "job") { EventId = cursor.ToString(CultureInfo.InvariantCulture) };
            }
        }
    }

    // A job by id or unique prefix; an ambiguous prefix is the caller's problem to fix.
    private static async Task<(JobDetails? Details, ProblemHttpResult? Problem)> FindJobAsync(ReviewStore store, string id, CancellationToken ct)
    {
        try
        {
            return (await store.GetJobAsync(id, ct).ConfigureAwait(false), null);
        }
        catch (InvalidOperationException e)
        {
            return (null, TypedResults.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest));
        }
    }

    private static bool TryHost(string? host, out Uri uri)
    {
        if (host is null)
        {
            uri = GitHubUrls.GitHubCom;
            return true;
        }

        return Uri.TryCreate(host, UriKind.Absolute, out uri!) && uri.Scheme is "https" or "http";
    }

    private static ProblemHttpResult ConfigProblem(ConfigException e, int status) =>
        TypedResults.Problem(
            e.Errors.Count == 1 ? e.Errors[0] : $"{ConfigLoader.FileName} has {e.Errors.Count} problems.",
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["errors"] = e.Errors });

    /// <summary>
    /// Runs <paramref name="use"/> with the account's provider: 404 for an unknown account, 422 when the config is
    /// invalid or the account can't connect (unless <paramref name="connectProblem"/> answers that), 502 when the
    /// provider's API fails.
    /// </summary>
    private static async Task<Results<Ok<T>, ProblemHttpResult>> WithProviderAsync<T>(
        EngineServices engine,
        string id,
        Func<AccountConfig, IRepositoryProvider, Task<T>> use,
        CancellationToken ct,
        Func<AccountConfig, string, T>? connectProblem = null)
    {
        AccountConfig? account = null;
        try
        {
            var config = await ConfigLoader.LoadAsync(engine.Paths.Config, ct).ConfigureAwait(false);
            account = config.Accounts.FirstOrDefault(a => a.Id == id);
            if (account is null)
            {
                return TypedResults.Problem($"No account with id '{id}'.", statusCode: StatusCodes.Status404NotFound);
            }

            if (engine.Providers is null)
            {
                return TypedResults.Problem("This engine can't connect to accounts.", statusCode: StatusCodes.Status501NotImplemented);
            }

            var provider = await engine.Providers(account, ct).ConfigureAwait(false);
            return TypedResults.Ok(await use(account, provider).ConfigureAwait(false));
        }
        catch (ConfigException e)
        {
            return ConfigProblem(e, StatusCodes.Status422UnprocessableEntity);
        }
        catch (InvalidOperationException e) when (account is not null)
        {
            return connectProblem is not null
                ? TypedResults.Ok(connectProblem(account, e.Message))
                : TypedResults.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ProviderApiException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Why the policy decided as it did, from the attempt's plan; empty before there's one.</summary>
    private static async Task<IReadOnlyList<string>> ReadReasonsAsync(string? attemptFolder, CancellationToken ct)
    {
        if (attemptFolder is null)
        {
            return [];
        }

        try
        {
            await using var file = File.OpenRead(Path.Combine(attemptFolder, ReviewRunner.PlanFile));
            using var plan = await System.Text.Json.JsonDocument.ParseAsync(file, cancellationToken: ct).ConfigureAwait(false);
            return plan.RootElement.TryGetProperty("reasons", out var reasons)
                ? [.. reasons.EnumerateArray().Select(r => r.GetString() ?? "")]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static async Task<Results<Ok<ConfigText>, ProblemHttpResult>> SaveConfigAsync(Func<Task<ConfigText>> save)
    {
        try
        {
            return TypedResults.Ok(await save().ConfigureAwait(false));
        }
        catch (ConfigConflictException e)
        {
            return TypedResults.Problem(
                e.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["currentHash"] = e.CurrentHash });
        }
        catch (ConfigException e)
        {
            return ConfigProblem(e, StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static ProblemHttpResult Conflict(string detail) => TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict);

    private static string Name(JobState state) => state.ToString().ToLowerInvariant();

    private sealed class EngineLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Passes the API's warnings and errors to the engine's log; the rest is noise for a local socket.</summary>
    private sealed class LogForwarder(Action<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(log);

        public void Dispose()
        {
        }

        private sealed class Logger(Action<string> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    log($"API: {formatter(state, exception)}{(exception is null ? "" : $" {exception.Message}")}");
                }
            }
        }
    }
}
