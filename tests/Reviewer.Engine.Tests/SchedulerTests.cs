using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;

namespace Reviewer.Engine.Tests;

public sealed class SchedulerTests : IDisposable
{
    private static readonly RepoRef Repo = new(new Uri("https://github.com"), "acme", "api");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-scheduler-");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeProvider _provider = new(new PullRequestSnapshot(
        new PullRequestRef(Repo, 1), "", "", "h1", "b1", "main", "alice", [], [], [], false, CiState.Unknown, true));
    private readonly List<string> _log = [];
    private readonly Source _source;
    private int _wakes;

    public SchedulerTests()
    {
        _source = new Source(_provider) { Config = ConfigLoader.Parse("repos:\n  - repo: acme/api\n") };
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<(ReviewStore Store, Scheduler Scheduler)> OpenAsync()
    {
        var store = await ReviewStore.OpenAsync(Path.Combine(_dir.FullName, ReviewStore.FileName), _time, Ct);
        return (store, new Scheduler(store, _source, _time, () => _wakes++, _log.Add));
    }

    private static PullRequestSummary Pr(int number, string head = "h1", string[]? reviewers = null, string[]? assignees = null, bool draft = false) =>
        new(new PullRequestRef(Repo, number), head, "main", "alice", assignees ?? [], reviewers ?? [], [], draft);

    private async Task PollAgainAsync(Scheduler scheduler, string etag)
    {
        _provider.ListETag = etag;
        _time.Now += TimeSpan.FromSeconds(60);
        await scheduler.TickAsync(Ct);
    }

    // A finished review of the PR at head, under the current config.
    private async Task ReviewedAsync(ReviewStore store, int number, string head)
    {
        var id = await store.StartJobAsync(
            new JobRecord
            {
                Id = $"done-{number}-{head}", Host = "https://github.com", Repo = "acme/api", Number = number, HeadSha = head,
                ConfigHash = _source.Config.For(Repo).Hash, AgentId = "claude-code", AppVersion = "test",
            },
            Ct);
        await store.SkipJobAsync(id, "test", Ct);
    }

    [Fact]
    public async Task FirstPoll_QueuesPrsAnInitialTriggerMatches()
    {
        var (store, scheduler) = await OpenAsync();
        _provider.Open.AddRange([Pr(1, reviewers: ["bob"]), Pr(2), Pr(3, assignees: ["Bob"])]);

        await scheduler.TickAsync(Ct);

        var queue = await store.ListQueueAsync(Ct);
        Assert.Equal([1, 3], queue.Select(j => j.Number).Order());
        Assert.All(queue, j => Assert.Null(j.NotBefore));
        Assert.Equal(_source.Config.For(Repo).Hash, queue[0].ConfigHash);
        Assert.Equal(2, _wakes);
    }

    [Fact]
    public async Task NextPoll_SendsTheETag_AndWaitsTheInterval()
    {
        var (store, scheduler) = await OpenAsync();
        _provider.Open.Add(Pr(1, reviewers: ["bob"]));

        await scheduler.TickAsync(Ct);
        _time.Now += TimeSpan.FromSeconds(30);
        await scheduler.TickAsync(Ct);
        _time.Now += TimeSpan.FromSeconds(30);
        await scheduler.TickAsync(Ct);

        Assert.Equal([null, "\"1\""], _provider.ListRequests);
        Assert.Single(await store.ListQueueAsync(Ct));
    }

    [Fact]
    public async Task PushToAReviewedPr_QueuesAfterTheDebounce()
    {
        var (store, scheduler) = await OpenAsync();
        await ReviewedAsync(store, 1, "h1");
        _provider.Open.Add(Pr(1));
        await scheduler.TickAsync(Ct);
        Assert.Empty(await store.ListQueueAsync(Ct));

        _provider.Open[0] = Pr(1, head: "h2");
        await PollAgainAsync(scheduler, "\"2\"");

        var job = Assert.Single(await store.ListQueueAsync(Ct));
        Assert.Equal(_time.Now.AddMinutes(5), job.NotBefore);
    }

    [Fact]
    public async Task PushWhileTheEngineWasStopped_IsQueuedOnTheFirstPoll()
    {
        var (store, scheduler) = await OpenAsync();
        await ReviewedAsync(store, 1, "h1");
        _provider.Open.Add(Pr(1, head: "h2"));

        await scheduler.TickAsync(Ct);

        Assert.Single(await store.ListQueueAsync(Ct));
    }

    [Fact]
    public async Task ReRequestWithoutNewCommits_IsNotQueued_WhenThatHeadWasReviewed()
    {
        var (store, scheduler) = await OpenAsync();
        await ReviewedAsync(store, 1, "h1");
        _provider.Open.Add(Pr(1));
        await scheduler.TickAsync(Ct);

        _provider.Open[0] = Pr(1, reviewers: ["bob"]);
        await PollAgainAsync(scheduler, "\"2\"");

        Assert.Empty(await store.ListQueueAsync(Ct));
    }

    [Fact]
    public async Task Draft_IsQueuedWhenMarkedReady()
    {
        var (store, scheduler) = await OpenAsync();
        _provider.Open.Add(Pr(1, reviewers: ["bob"], draft: true));
        await scheduler.TickAsync(Ct);
        Assert.Empty(await store.ListQueueAsync(Ct));

        _provider.Open[0] = Pr(1, reviewers: ["bob"]);
        await PollAgainAsync(scheduler, "\"2\"");

        Assert.Single(await store.ListQueueAsync(Ct));
    }

    [Fact]
    public async Task OversizedPr_IsLoggedNotQueued_AndCheckedAgainOnAPush()
    {
        var (store, scheduler) = await OpenAsync();
        _provider.Open.Add(Pr(1, reviewers: ["bob"]));
        _provider.ChangedFiles[1] = [new ChangedFile("src/big.cs", 5000, 0)];
        await scheduler.TickAsync(Ct);
        await PollAgainAsync(scheduler, "\"2\"");

        Assert.Empty(await store.ListQueueAsync(Ct));
        Assert.Single(_log, l => l.Contains("maxDiffLines", StringComparison.Ordinal));

        _provider.ChangedFiles.Remove(1);
        _provider.Open[0] = Pr(1, head: "h2", reviewers: ["bob"]);
        await PollAgainAsync(scheduler, "\"3\"");

        Assert.Single(await store.ListQueueAsync(Ct));
    }

    [Fact]
    public async Task ClosedPr_IsForgotten()
    {
        var (store, scheduler) = await OpenAsync();
        _provider.Open.AddRange([Pr(1), Pr(2)]);
        await scheduler.TickAsync(Ct);

        _provider.Open.RemoveAt(0);
        await PollAgainAsync(scheduler, "\"2\"");

        Assert.Equal([2], (await store.GetSeenAsync(Repo, Ct)).Keys);
    }

    [Fact]
    public async Task ChangedTriggers_ReadTheListAgain()
    {
        var (store, scheduler) = await OpenAsync();
        _provider.Open.Add(Pr(1));
        await scheduler.TickAsync(Ct);
        Assert.Empty(await store.ListQueueAsync(Ct));

        _source.Config = ConfigLoader.Parse("defaults:\n  triggers:\n    initial: [all]\nrepos:\n  - repo: acme/api\n");
        await PollAgainAsync(scheduler, "\"1\"");

        Assert.Equal([null, null], _provider.ListRequests);
        Assert.Single(await store.ListQueueAsync(Ct));
    }

    [Fact]
    public async Task FailedPoll_IsLoggedOnce_AndKeepsTheETag()
    {
        var (_, scheduler) = await OpenAsync();
        await scheduler.TickAsync(Ct);

        _provider.ListError = new HttpRequestException("offline");
        await PollAgainAsync(scheduler, "\"1\"");
        await PollAgainAsync(scheduler, "\"1\"");
        _provider.ListError = null;
        await PollAgainAsync(scheduler, "\"1\"");

        Assert.Equal([null, "\"1\"", "\"1\"", "\"1\""], _provider.ListRequests);
        Assert.Single(_log, l => l.Contains("offline", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("works again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidConfig_KeepsPollingWithTheLastOne()
    {
        var (store, scheduler) = await OpenAsync();
        await scheduler.TickAsync(Ct);

        _source.Error = new ConfigException(["config.yaml:1: oops"]);
        _provider.Open.Add(Pr(1, reviewers: ["bob"]));
        await PollAgainAsync(scheduler, "\"2\"");

        Assert.Single(await store.ListQueueAsync(Ct));
        Assert.Contains(_log, l => l.Contains("oops", StringComparison.Ordinal));
    }

    private sealed class Source(IRepositoryProvider provider) : IPollSource
    {
        public required ReviewerConfig Config { get; set; }

        public ConfigException? Error { get; set; }

        public Task<ReviewerConfig> LoadConfigAsync(CancellationToken ct) =>
            Error is { } error ? Task.FromException<ReviewerConfig>(error) : Task.FromResult(Config);

        public Task<IRepositoryProvider> GetProviderAsync(AccountConfig account, CancellationToken ct) => Task.FromResult(provider);
    }
}
