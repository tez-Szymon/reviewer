using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Engine;
using Reviewer.Engine.State;

namespace Reviewer.Api.Tests;

/// <summary>The API on a real Unix socket, as <c>reviewer serve</c> runs it, with <see cref="EngineClient"/> as the client.</summary>
public sealed class LocalApiTests : IAsyncLifetime, IDisposable
{
    private const string PrUrl = "https://github.com/acme/api/pull/7";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rv-api-");
    private ReviewerPaths _paths = null!;
    private ReviewStore _store = null!;
    private WebApplication _app = null!;
    private EngineClient _client = null!;
    private int _wakes;

    private static CancellationToken Ct => CancellationToken.None;

    private HttpClient Http => _client.Http;

    public async Task InitializeAsync()
    {
        _paths = new ReviewerPaths(_dir.FullName);
        _store = await ReviewStore.OpenAsync(_paths.Database, TimeProvider.System, Ct);
        var engine = new EngineServices(_paths, _store, [new FakeAgent()], () => Interlocked.Increment(ref _wakes), TimeProvider.System, DateTimeOffset.UtcNow);
        _app = await LocalApi.StartAsync(engine, _paths.Socket, log: null, Ct, eventPollInterval: TimeSpan.FromMilliseconds(50));
        _client = new EngineClient(_paths.Socket);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    // xunit calls this after DisposeAsync.
    public void Dispose()
    {
        _client.Dispose();
        _dir.Delete(recursive: true);
    }

    private static JobRecord Job(string id, int number = 7) => new()
    {
        Id = id, Host = "https://github.com", Repo = "acme/api", Number = number, ConfigHash = "h", AgentId = "claude-code", AppVersion = "test",
    };

    private Task<HttpResponseMessage> PostAsync(string path, object? body = null) =>
        Http.PostAsJsonAsync(path, body ?? new { }, ApiJson.Options, Ct);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, Ct))!;

    [Fact]
    public async Task Status_DescribesThisEngine()
    {
        var status = await _client.GetStatusAsync(Ct);

        Assert.NotNull(status);
        Assert.Equal(Environment.ProcessId, status.ProcessId);
        Assert.Equal(_dir.FullName, status.DataFolder);
    }

    [Fact]
    public async Task Status_WithoutAnEngine_IsNull()
    {
        using var nobody = new EngineClient(Path.Combine(_dir.FullName, "none.sock"));

        Assert.Null(await nobody.GetStatusAsync(Ct));
    }

    [Fact]
    public void Socket_IsTheCurrentUsersOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // No Unix file modes; the socket file inherits the data folder's ACL.
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.Socket));
    }

    [Fact]
    public async Task Agents_ReportsEachAgentsHealth()
    {
        var agents = await Http.GetFromJsonAsync<List<AgentStatus>>("agents", ApiJson.Options, Ct);

        Assert.Equal([new AgentStatus("fake", false, true, false, "1.0", "Not logged in.")], agents);
    }

    [Fact]
    public async Task QueueReview_AddsAJob_ThenMergesARepeat_AndWakesTheWorker()
    {
        using var first = await PostAsync("jobs", new QueueRequest(PrUrl, DryRun: true));
        using var second = await PostAsync("jobs", new QueueRequest(PrUrl, DryRun: true, Full: true));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var added = await ReadAsync<QueueResult>(first);
        Assert.Equal(new QueueResult(added.JobId, Added: false), await ReadAsync<QueueResult>(second));
        Assert.Equal($"/jobs/{added.JobId}", first.Headers.Location?.ToString());
        Assert.Equal(2, _wakes);

        var queue = await Http.GetFromJsonAsync<List<JobDto>>("queue", ApiJson.Options, Ct);
        var job = Assert.Single(queue!);
        Assert.Equal((JobState.Queued, true, true), (job.State, job.DryRun, job.Full));
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/api/-/merge_requests/7")]
    [InlineData("not a url")]
    public async Task QueueReview_ForAnUnsupportedUrl_IsABadRequest(string url)
    {
        using var response = await PostAsync("jobs", new QueueRequest(url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _wakes);
    }

    [Fact]
    public async Task QueueReview_ForAGitLabMergeRequest_UsesTheAccountsHost()
    {
        await File.WriteAllTextAsync(_paths.Config, "accounts: [{ id: work, provider: gitlab, host: https://git.acme.test/gitlab }]\n", Ct);

        using var response = await PostAsync("jobs", new QueueRequest("https://git.acme.test/gitlab/acme/platform/api/-/merge_requests/12"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = Assert.Single((await Http.GetFromJsonAsync<List<JobDto>>("queue", ApiJson.Options, Ct))!);
        Assert.Equal(("https://git.acme.test", "acme/platform/api", 12), (job.Host, job.Repo, job.Number));
    }

    [Fact]
    public async Task QueueReview_WithAnInvalidConfig_ListsItsErrors()
    {
        await File.WriteAllTextAsync(_paths.Config, "defaults:\n  review: { mode: nope }\n", Ct);

        using var response = await PostAsync("jobs", new QueueRequest(PrUrl));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("mode", problem.RootElement.GetProperty("errors")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_QueuedJob_ByItsUniqueStart()
    {
        await _store.EnqueueAsync(Job("20261102-1200-acme-api-7"), Ct);

        using var response = await PostAsync("jobs/20261102-12/cancel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new CancelResult("20261102-1200-acme-api-7", JobState.Cancelled), await ReadAsync<CancelResult>(response));
        Assert.Equal(1, _wakes);
    }

    [Fact]
    public async Task Cancel_RunningJob_AsksTheWorkerToStopIt()
    {
        await _store.EnqueueAsync(Job("j1"), Ct);
        await _store.TryClaimJobAsync("j1", Ct);

        using var response = await PostAsync("jobs/j1/cancel");

        Assert.Equal(new CancelResult("j1", JobState.Running), await ReadAsync<CancelResult>(response));
        Assert.Equal(["j1"], await _store.GetCancelRequestsAsync(["j1"], Ct));
    }

    [Fact]
    public async Task Cancel_FinishedJob_IsAConflict_AndAnAmbiguousStartABadRequest()
    {
        await _store.StartJobAsync(Job("j1"), Ct);
        await _store.FailJobAsync("j1", JobState.Cancelled, "Cancelled.", null, Ct);
        await _store.EnqueueAsync(Job("j2", number: 8), Ct);

        using var finished = await PostAsync("jobs/j1/cancel");
        using var ambiguous = await PostAsync("jobs/j/cancel");
        using var missing = await PostAsync("jobs/nope/cancel");

        Assert.Equal(HttpStatusCode.Conflict, finished.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Retry_FailedJob_QueuesItAgain_ButNotARunningOne()
    {
        await _store.StartJobAsync(Job("j1"), Ct);
        await _store.FailJobAsync("j1", JobState.Failed, "boom", null, Ct);
        await _store.StartJobAsync(Job("j2", number: 8), Ct);

        using var failed = await PostAsync("jobs/j1/retry");
        using var running = await PostAsync("jobs/j2/retry");

        Assert.Equal(JobState.Queued, (await ReadAsync<JobDto>(failed)).State);
        Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);
        Assert.Equal(1, _wakes);
    }

    [Fact]
    public async Task Jobs_ListsHistory_AndGetsOneWithItsFindings()
    {
        await _store.StartJobAsync(Job("j1"), Ct);
        await _store.StartJobAsync(Job("j2", number: 8), Ct);

        var all = await Http.GetFromJsonAsync<List<JobSummaryDto>>("jobs?repo=acme/api", ApiJson.Options, Ct);
        var one = await Http.GetFromJsonAsync<List<JobSummaryDto>>("jobs?repo=acme/api&number=8", ApiJson.Options, Ct);
        var details = await Http.GetFromJsonAsync<JobDetailsDto>("jobs/j1", ApiJson.Options, Ct);
        using var missing = await Http.GetAsync("jobs/nope", Ct);
        using var badLimit = await Http.GetAsync("jobs?limit=0", Ct);

        Assert.Equal(2, all!.Count);
        Assert.Equal("j2", Assert.Single(one!).Job.Id);
        Assert.Equal(("j1", JobState.Running), (details!.Job.Id, details.Job.State));
        Assert.Empty(details.Findings);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
    }

    [Fact]
    public async Task PullRequest_WithoutAReview_ListsItsJobsOnly()
    {
        await _store.StartJobAsync(Job("j1"), Ct);

        var pr = await Http.GetFromJsonAsync<PullRequestDto>("pulls/ACME/api/7", ApiJson.Options, Ct);

        Assert.Equal(("https://github.com", "ACME/api", 7), (pr!.Host, pr.Repo, pr.Number));
        Assert.Null(pr.LastReview);
        Assert.Empty(pr.OpenFindings);
        Assert.Equal("j1", Assert.Single(pr.Jobs).Job.Id);
    }

    [Fact]
    public async Task Config_ShowsTheMergedConfig_AndARepoEffectiveOne()
    {
        await File.WriteAllTextAsync(_paths.Config, "repos:\n  - repo: acme/api\n    overrides:\n      review: { mode: gate }\n", Ct);

        using var config = JsonDocument.Parse(await Http.GetStringAsync("config", Ct));
        using var repo = JsonDocument.Parse(await Http.GetStringAsync("config/repos/acme/api", Ct));

        Assert.Equal("acme/api", config.RootElement.GetProperty("repos")[0].GetProperty("repo").GetString());
        Assert.Equal("gate", repo.RootElement.GetProperty("policy").GetProperty("review").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Config_WhenTheFileIsInvalid_IsUnprocessable()
    {
        await File.WriteAllTextAsync(_paths.Config, "nope: 1\n", Ct);

        using var response = await Http.GetAsync("config", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ValidateConfig_ReportsErrorsWithoutSaving()
    {
        using var bad = await Http.PostAsync("config/validate", new StringContent("defaults:\n  review: { mode: nope }\n", Encoding.UTF8, "application/yaml"), Ct);
        using var good = await Http.PostAsync("config/validate", new StringContent("defaults:\n  review: { mode: gate }\n", Encoding.UTF8, "text/plain"), Ct);

        var invalid = await ReadAsync<ConfigValidation>(bad);
        Assert.False(invalid.Valid);
        Assert.Contains("mode", Assert.Single(invalid.Errors), StringComparison.Ordinal);
        var valid = await ReadAsync<ConfigValidation>(good);
        Assert.True(valid.Valid);
        Assert.Empty(valid.Errors);
        Assert.False(File.Exists(_paths.Config));
    }

    [Fact]
    public async Task OpenApi_DescribesTheEndpoints()
    {
        using var spec = JsonDocument.Parse(await Http.GetStringAsync("openapi/v1.json", Ct));

        Assert.True(spec.RootElement.GetProperty("paths").TryGetProperty("/events", out _));
    }

    [Fact]
    public async Task Events_StreamChangesFromNowOn_IncludingOtherProcesses()
    {
        await _store.EnqueueAsync(Job("old"), Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var events = _client.WatchJobsAsync(after: null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync();
        await Task.Delay(200, Ct); // Let the stream pick its starting point before anything changes.

        // Another store on the same file, as the CLI would write.
        var cli = await ReviewStore.OpenAsync(_paths.Database, TimeProvider.System, Ct);
        await cli.EnqueueAsync(Job("new", number: 8), Ct);

        Assert.True(await next);
        Assert.Equal(("new", JobState.Queued), (events.Current.Id, events.Current.State));

        await cli.CancelJobAsync("new", Ct);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(("new", JobState.Cancelled), (events.Current.Id, events.Current.State));
    }

    [Fact]
    public async Task Events_ResumeAfterAVersion()
    {
        await _store.EnqueueAsync(Job("j1"), Ct);
        await _store.EnqueueAsync(Job("j2", number: 8), Ct);
        var first = (await _store.GetJobAsync("j1", Ct))!.Job.Version;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await using var events = _client.WatchJobsAsync(after: first, timeout.Token).GetAsyncEnumerator(timeout.Token);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("j2", events.Current.Id);
    }

    [Fact]
    public async Task Events_EndWhenTheApiStops()
    {
        var stream = Task.Run(async () =>
        {
            await foreach (var _ in _client.WatchJobsAsync(after: null, Ct))
            {
            }
        }, Ct);
        await Task.Delay(200, Ct);

        await _app.StopAsync(Ct);

        var ended = await Task.WhenAny(stream, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        Assert.Same(stream, ended);
    }

    private sealed class FakeAgent : IReviewAgent
    {
        public string Id => "fake";

        public AgentCapabilities Capabilities { get; } = new(NeedsWorktree: false, MaxConcurrency: 1, SupportsSchemaFlag: false);

        public Task<AgentHealth> CheckAsync(CancellationToken ct) => Task.FromResult(new AgentHealth(true, false, "1.0", "Not logged in."));

        public Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct) => throw new NotSupportedException();
    }
}
