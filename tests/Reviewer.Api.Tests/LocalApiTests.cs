using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
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
    private readonly FakeProvider _provider = new();
    private Exception? _connectError;

    private static CancellationToken Ct => CancellationToken.None;

    private HttpClient Http => _client.Http;

    public async Task InitializeAsync()
    {
        _paths = new ReviewerPaths(_dir.FullName);
        _store = await ReviewStore.OpenAsync(_paths.Database, TimeProvider.System, Ct);
        var engine = new EngineServices(
            _paths, _store, [new FakeAgent()], () => Interlocked.Increment(ref _wakes), TimeProvider.System, DateTimeOffset.UtcNow,
            (_, _) => _connectError is { } e ? Task.FromException<IRepositoryProvider>(e) : Task.FromResult<IRepositoryProvider>(_provider));
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

    [Theory]
    [InlineData("codex", HttpStatusCode.Created)]
    [InlineData("gemini", HttpStatusCode.BadRequest)]
    public async Task QueueReview_WithAnotherAgent_QueuesItIfKnown(string agent, HttpStatusCode expected)
    {
        using var response = await PostAsync("jobs", new QueueRequest(PrUrl, Agent: agent));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Created)
        {
            Assert.Equal(agent, Assert.Single((await Http.GetFromJsonAsync<List<JobDto>>("queue", ApiJson.Options, Ct))!).AgentId);
        }
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
    public async Task ConfigFile_WithoutAFile_IsEmpty()
    {
        var file = await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct);

        Assert.Equal("", file!.Yaml);
        Assert.NotEmpty(file.Hash);
    }

    [Fact]
    public async Task EditConfig_ChangesOnlyThoseSpans_AndReturnsTheNewHash()
    {
        await File.WriteAllTextAsync(_paths.Config, "# mine\ndefaults:\n  review:\n    mode: comment   # for now\n", Ct);
        var file = (await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct))!;

        using var response = await Http.PatchAsJsonAsync("config", new
        {
            baseHash = file.Hash,
            edits = new object[]
            {
                new { op = "set", path = "defaults.review.mode", value = "gate" },
                new { op = "add", path = "repos", value = new { repo = "acme/api" } },
            },
        }, ApiJson.Options, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadAsync<ConfigText>(response);
        Assert.Equal("# mine\ndefaults:\n  review:\n    mode: gate   # for now\nrepos:\n  - { repo: acme/api }\n", saved.Yaml);
        Assert.Equal(saved.Yaml, await File.ReadAllTextAsync(_paths.Config, Ct));
        Assert.Equal(saved, await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct));
    }

    [Fact]
    public async Task EditConfig_AfterAHandEdit_IsAConflict_AndWritesNothing()
    {
        var file = (await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct))!;
        await File.WriteAllTextAsync(_paths.Config, "agents: { default: codex }\n", Ct);

        using var response = await Http.PatchAsJsonAsync(
            "config", new { baseHash = file.Hash, edits = new[] { new { op = "set", path = "agents.default", value = "claude-code" } } }, ApiJson.Options, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var current = (await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct))!;
        Assert.Equal(current.Hash, problem.RootElement.GetProperty("currentHash").GetString());
        Assert.Equal("agents: { default: codex }\n", current.Yaml);
    }

    [Theory]
    [InlineData("set", "defaults.review.mode", "nope", "defaults.review.mode")]
    [InlineData("remove", "repos[3]", null, "repos[3] doesn't exist.")]
    public async Task EditConfig_ThatBreaksIt_IsUnprocessable_AndWritesNothing(string op, string path, string? value, string error)
    {
        var file = (await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct))!;

        using var response = await Http.PatchAsJsonAsync("config", new { baseHash = file.Hash, edits = new[] { new { op, path, value } } }, ApiJson.Options, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.Config));
    }

    [Fact]
    public async Task SaveConfig_ReplacesTheFile_WhenValid()
    {
        var file = (await Http.GetFromJsonAsync<ConfigText>("config/file", ApiJson.Options, Ct))!;

        using var bad = await Http.PutAsJsonAsync("config/file", new ConfigSave("defaults: { review: { mode: nope } }\n", file.Hash), ApiJson.Options, Ct);
        using var good = await Http.PutAsJsonAsync("config/file", new ConfigSave("# hand-written\nagents: { default: codex }\n", file.Hash), ApiJson.Options, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("# hand-written\nagents: { default: codex }\n", await File.ReadAllTextAsync(_paths.Config, Ct));
    }

    [Fact]
    public async Task Accounts_ListTheConfiguredOnes_WithTokenReferencesOnly()
    {
        await File.WriteAllTextAsync(_paths.Config, "accounts: [{ id: gh, token: 'env:GH_WORK' }, { id: gl, provider: gitlab }]\n", Ct);

        var accounts = await Http.GetFromJsonAsync<List<AccountDto>>("accounts", ApiJson.Options, Ct);

        Assert.Equal(
            [new AccountDto("gh", "github", new Uri("https://github.com"), "env:GH_WORK"), new AccountDto("gl", "gitlab", new Uri("https://gitlab.com"), null)],
            accounts);
    }

    [Fact]
    public async Task AccountStatus_SaysWhoseTokenItIs_OrWhyItFails()
    {
        var ok = await Http.GetFromJsonAsync<AccountStatus>("accounts/github/status", ApiJson.Options, Ct);
        _provider.Error = FakeProvider.Unauthorized();
        var rejected = await Http.GetFromJsonAsync<AccountStatus>("accounts/github/status", ApiJson.Options, Ct);
        _connectError = new InvalidOperationException("No token for github.");
        var missing = await Http.GetFromJsonAsync<AccountStatus>("accounts/github/status", ApiJson.Options, Ct);

        Assert.Equal(new AccountStatus("github", true, "reviewer-bot", null), ok);
        Assert.False(rejected!.SignedIn);
        Assert.Contains("Bad credentials", rejected.Problem, StringComparison.Ordinal);
        Assert.Equal(new AccountStatus("github", false, null, "No token for github."), missing);
    }

    [Fact]
    public async Task AccountRepos_MarksTheWatchedOnes()
    {
        await File.WriteAllTextAsync(_paths.Config, "repos: [{ repo: ACME/api }]\n", Ct);
        _provider.Repos.AddRange([new RepoRef(new Uri("https://github.com"), "acme", "api"), new RepoRef(new Uri("https://github.com"), "acme", "web")]);

        var repos = await Http.GetFromJsonAsync<List<RepoChoice>>("accounts/github/repos?owner=acme", ApiJson.Options, Ct);

        Assert.Equal([new RepoChoice("acme/api", true), new RepoChoice("acme/web", false)], repos);
        Assert.Equal("acme", Assert.Single(_provider.Listed));
    }

    [Theory]
    [InlineData("accounts/nobody/repos", HttpStatusCode.NotFound)]
    [InlineData("accounts/github/repos", HttpStatusCode.BadGateway)]
    public async Task AccountRepos_Failures(string path, HttpStatusCode status)
    {
        _provider.Error = FakeProvider.Unauthorized();

        using var response = await Http.GetAsync(path, Ct);

        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task Pulls_ListWatchedOpenPrs_WithTitlesAndTheirNewestJob()
    {
        var repo = new RepoRef(new Uri("https://github.com"), "acme", "api");
        PullRequestSummary Open(int number, string title) =>
            new(new PullRequestRef(repo, number), $"head{number}", "main", "alice", [], ["reviewer-bot"], [], false, title);
        await _store.SaveSeenAsync(
            repo,
            new Dictionary<int, Engine.SeenPullRequest> { [7] = new("head7", true, false, true), [8] = new("head8", false, false, false) },
            [Open(7, "Fix totals"), Open(8, "Docs"), Open(9, "Draft, filtered out")],
            Ct);
        await _store.StartJobAsync(Job("old", 7), Ct);
        await Task.Delay(10, Ct);
        await _store.StartJobAsync(Job("new", 7), Ct);

        var pulls = (await Http.GetFromJsonAsync<List<WatchedPullRequestDto>>("pulls?repo=acme/api", ApiJson.Options, Ct))!;

        Assert.Equal([(8, "Docs", false), (7, "Fix totals", true)], pulls.Select(p => (p.Number, p.Title, p.Matched)));
        Assert.Equal("new", pulls[1].LatestJob!.Job.Id);
        Assert.Null(pulls[0].LatestJob);
        Assert.Null(pulls[1].LastReview);
        Assert.Empty((await Http.GetFromJsonAsync<List<WatchedPullRequestDto>>("pulls?repo=acme/web", ApiJson.Options, Ct))!);
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
