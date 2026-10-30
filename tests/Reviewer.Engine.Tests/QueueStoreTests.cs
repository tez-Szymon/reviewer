using Reviewer.Engine.State;

namespace Reviewer.Engine.Tests;

public sealed class QueueStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-queue-");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Delete(recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    private Task<ReviewStore> OpenAsync() => ReviewStore.OpenAsync(Path.Combine(_dir.FullName, ReviewStore.FileName), _time, Ct);

    private static JobRecord Job(string id, string repo = "acme/api", bool dryRun = false, bool full = false, DateTimeOffset? notBefore = null) => new()
    {
        Id = id, Host = "https://github.com", Repo = repo, Number = 7, ConfigHash = "h", AgentId = "claude-code", AppVersion = "test",
        DryRun = dryRun, Full = full, NotBefore = notBefore,
    };

    private static async Task<JobRecord> GetAsync(ReviewStore store, string id) => (await store.GetJobAsync(id, Ct))!.Job;

    [Fact]
    public async Task Enqueue_SecondRequestForQueuedPr_MergesIntoTheFirst()
    {
        var store = await OpenAsync();
        var later = _time.Now.AddMinutes(5);

        var first = await store.EnqueueAsync(Job("j1", notBefore: _time.Now.AddMinutes(1)), Ct);
        var second = await store.EnqueueAsync(Job("j2", repo: "ACME/Api", full: true, notBefore: later), Ct);

        Assert.Equal(new EnqueueResult("j1", Added: true), first);
        Assert.Equal(new EnqueueResult("j1", Added: false), second);
        var job = Assert.Single(await store.ListQueueAsync(Ct));
        Assert.Equal(JobState.Queued, job.State);
        Assert.True(job.Full);
        Assert.Equal(later, job.NotBefore);
    }

    [Fact]
    public async Task Enqueue_DryRunNextToARealRun_AddsAnotherJob()
    {
        var store = await OpenAsync();

        await store.EnqueueAsync(Job("j1"), Ct);
        var dry = await store.EnqueueAsync(Job("j2", dryRun: true), Ct);

        Assert.True(dry.Added);
        Assert.Equal(2, (await store.ListQueueAsync(Ct)).Count);
    }

    [Fact]
    public async Task Enqueue_WhileThePrIsRunning_QueuesAnother()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        await store.TryClaimJobAsync("j1", Ct);

        var next = await store.EnqueueAsync(Job("j2"), Ct);

        Assert.Equal(new EnqueueResult("j2", Added: true), next);
    }

    [Fact]
    public async Task TryClaim_SucceedsOnce_AndRecordsThisProcess()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);

        Assert.True(await store.TryClaimJobAsync("j1", Ct));
        Assert.False(await store.TryClaimJobAsync("j1", Ct));

        var job = await GetAsync(store, "j1");
        Assert.Equal(JobState.Running, job.State);
        Assert.Equal(Environment.ProcessId, job.RunnerPid);
        Assert.Equal(_time.Now, job.StartedAt);
    }

    [Fact]
    public async Task TryClaim_ThrottledJob_ClearsItsRetry()
    {
        var store = await OpenAsync();
        await store.StartJobAsync(Job("j1"), Ct);
        await store.FailJobAsync("j1", JobState.Throttled, "limit", _time.Now.AddHours(1), Ct);

        Assert.True(await store.TryClaimJobAsync("j1", Ct));

        var job = await GetAsync(store, "j1");
        Assert.Null(job.RetryAt);
        Assert.Null(job.Error);
        Assert.Null(job.FinishedAt);
    }

    [Fact]
    public async Task Cancel_QueuedJob_IsCancelledAtOnce()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);

        Assert.Equal(JobState.Cancelled, await store.CancelJobAsync("j1", Ct));
        Assert.Empty(await store.ListQueueAsync(Ct));
        Assert.False(await store.TryClaimJobAsync("j1", Ct));
    }

    [Fact]
    public async Task Cancel_RunningJob_OnlyAsksItsWorker()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        await store.EnqueueAsync(Job("j2", repo: "acme/web"), Ct);
        await store.TryClaimJobAsync("j1", Ct);
        await store.TryClaimJobAsync("j2", Ct);

        Assert.Equal(JobState.Running, await store.CancelJobAsync("j1", Ct));

        Assert.Equal(["j1"], await store.GetCancelRequestsAsync(["j1", "j2"], Ct));
    }

    [Fact]
    public async Task Cancel_FinishedOrMissingJob_ChangesNothing()
    {
        var store = await OpenAsync();
        await store.StartJobAsync(Job("j1"), Ct);
        await store.FailJobAsync("j1", JobState.Failed, "boom", null, Ct);

        Assert.Equal(JobState.Failed, await store.CancelJobAsync("j1", Ct));
        Assert.Null(await store.CancelJobAsync("nope", Ct));
        Assert.Equal("boom", (await GetAsync(store, "j1")).Error);
    }

    [Theory]
    [InlineData(JobState.Failed, true)]
    [InlineData(JobState.Cancelled, true)]
    [InlineData(JobState.Throttled, true)]
    [InlineData(JobState.Running, false)]
    public async Task Retry_QueuesFinishedJobsAgain_ButNotRunningOnes(JobState state, bool retried)
    {
        var store = await OpenAsync();
        await store.StartJobAsync(Job("j1"), Ct);
        if (state != JobState.Running)
        {
            await store.FailJobAsync("j1", state, "why", _time.Now.AddHours(1), Ct);
        }

        Assert.Equal(retried, await store.RetryJobAsync("j1", Ct));

        var job = await GetAsync(store, "j1");
        Assert.Equal(retried ? JobState.Queued : state, job.State);
        if (retried)
        {
            Assert.Null(job.Error);
            Assert.Null(job.RetryAt);
        }
    }

    [Fact]
    public async Task Retry_SucceededJob_IsRefused()
    {
        var store = await OpenAsync();
        await store.StartJobAsync(Job("j1"), Ct);
        await store.SkipJobAsync("j1", "nothing new", Ct);

        Assert.False(await store.RetryJobAsync("j1", Ct));
    }

    [Fact]
    public async Task RequeueInterrupted_LeavesJobsSomeoneCancelled()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        await store.EnqueueAsync(Job("j2", repo: "acme/web"), Ct);
        await store.TryClaimJobAsync("j1", Ct);
        await store.TryClaimJobAsync("j2", Ct);
        await store.CancelJobAsync("j2", Ct);
        await store.EndIfRunningAsync("j1", JobState.Cancelled, "Cancelled.", Ct);
        await store.EndIfRunningAsync("j2", JobState.Cancelled, "Cancelled.", Ct);

        Assert.True(await store.RequeueInterruptedJobAsync("j1", Ct));
        Assert.False(await store.RequeueInterruptedJobAsync("j2", Ct));

        Assert.Equal(JobState.Queued, (await GetAsync(store, "j1")).State);
        Assert.Equal(JobState.Cancelled, (await GetAsync(store, "j2")).State);
    }

    [Fact]
    public async Task EndIfRunning_LeavesARecordedResult()
    {
        var store = await OpenAsync();
        await store.StartJobAsync(Job("j1"), Ct);
        await store.SkipJobAsync("j1", "nothing new", Ct);

        Assert.False(await store.EndIfRunningAsync("j1", JobState.Failed, "late", Ct));
        Assert.Equal(JobState.Skipped, (await GetAsync(store, "j1")).State);
    }

    [Fact]
    public async Task FailOrphaned_FailsOnlyJobsWhoseProcessIsGone()
    {
        var store = await OpenAsync();
        await store.StartJobAsync(Job("dead"), Ct);
        await store.StartJobAsync(Job("alive", repo: "acme/web"), Ct);

        Assert.Equal(1, await store.FailOrphanedJobsAsync(j => j.Id == "alive", Ct));

        var dead = await GetAsync(store, "dead");
        Assert.Equal(JobState.Failed, dead.State);
        Assert.Contains("stopped before it finished", dead.Error, StringComparison.Ordinal);
        Assert.Equal(JobState.Running, (await GetAsync(store, "alive")).State);
    }
}
