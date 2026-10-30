using System.Diagnostics;
using Reviewer.Engine.State;

namespace Reviewer.Engine.Tests;

public sealed class QueueWorkerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-worker-");
    private readonly ManualTime _time = new(Now);

    public void Dispose() => _dir.Delete(recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    private static JobRecord Job(
        string id, JobState state = JobState.Queued, string repo = "acme/api", string agent = "claude-code", int minute = 0) => new()
    {
        Id = id, Host = "https://github.com", Repo = repo, Number = 7, ConfigHash = "h", AgentId = agent, AppVersion = "test",
        State = state, CreatedAt = Now.AddMinutes(minute),
    };

    private static IReadOnlyList<string> Due(IReadOnlyList<JobRecord> active, int limit = 1) =>
        [.. QueueWorker.Pick(active, Now, _ => limit).Due.Select(j => j.Id)];

    [Fact]
    public void Pick_StartsDueJobsOldestFirst_AndWaitsForNotBefore()
    {
        var later = Job("later", repo: "acme/c", minute: -10);
        later.NotBefore = Now.AddMinutes(3);
        var active = new[] { Job("second", repo: "acme/b", minute: -1), Job("first", repo: "acme/a", minute: -2), later };

        var (due, next) = QueueWorker.Pick(active, Now, _ => 5);

        Assert.Equal(["first", "second"], due.Select(j => j.Id));
        Assert.Equal(Now.AddMinutes(3), next);
    }

    [Fact]
    public void Pick_OneJobPerPr_EvenAcrossAgents()
    {
        Assert.Equal(["a"], Due([Job("a", minute: -2), Job("b", agent: "codex", minute: -1)], limit: 5));
        Assert.Empty(Due([Job("running", JobState.Running, repo: "ACME/API"), Job("waiting")], limit: 5));
    }

    [Fact]
    public void Pick_KeepsToTheAgentsConcurrency()
    {
        JobRecord[] active = [Job("running", JobState.Running, repo: "acme/x"), Job("a", repo: "acme/a", minute: -2), Job("b", repo: "acme/b", minute: -1)];

        Assert.Empty(Due(active, limit: 1));
        Assert.Equal(["a"], Due(active, limit: 2));
        Assert.Equal(["a", "b"], Due(active, limit: 3));
    }

    [Fact]
    public void Pick_ThrottledJob_PausesItsAgentUntilItsRetry()
    {
        var throttled = Job("throttled", JobState.Throttled, repo: "acme/t", minute: -5);
        throttled.RetryAt = Now.AddMinutes(30);
        JobRecord[] active = [throttled, Job("same-agent", repo: "acme/a"), Job("other-agent", repo: "acme/b", agent: "codex")];

        var (due, next) = QueueWorker.Pick(active, Now, _ => 5);

        Assert.Equal(["other-agent"], due.Select(j => j.Id));
        Assert.Equal(Now.AddMinutes(30), next);
        Assert.Equal(["other-agent", "throttled", "same-agent"], QueueWorker.Pick(active, Now.AddMinutes(30), _ => 5).Due.Select(j => j.Id));
    }

    [Fact]
    public void Pick_ThrottleWithoutRetryTime_WaitsTheBackoff()
    {
        var throttled = Job("throttled", JobState.Throttled);
        throttled.FinishedAt = Now;

        Assert.Equal(Now + QueueWorker.ThrottleBackoff, QueueWorker.Pick([throttled], Now, _ => 1).Next);
        Assert.Single(QueueWorker.Pick([throttled], Now + QueueWorker.ThrottleBackoff, _ => 1).Due);
    }

    [Fact]
    public void IsRunning_KnowsThisProcess_AndRejectsReusedOrMissingIds()
    {
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        Assert.True(QueueWorker.IsRunning(new JobRecord { Id = "a", Host = "h", Repo = "r", ConfigHash = "h", AgentId = "a", AppVersion = "v", RunnerPid = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow }));
        Assert.False(QueueWorker.IsRunning(new JobRecord { Id = "b", Host = "h", Repo = "r", ConfigHash = "h", AgentId = "a", AppVersion = "v", RunnerPid = Environment.ProcessId, StartedAt = started.AddMinutes(-5) }));
        Assert.False(QueueWorker.IsRunning(new JobRecord { Id = "c", Host = "h", Repo = "r", ConfigHash = "h", AgentId = "a", AppVersion = "v" }));
    }

    private async Task<ReviewStore> OpenAsync() => await ReviewStore.OpenAsync(Path.Combine(_dir.FullName, ReviewStore.FileName), _time, Ct);

    private static async Task<JobRecord> GetAsync(ReviewStore store, string id) => (await store.GetJobAsync(id, Ct))!.Job;

    [Fact]
    public async Task Tick_RunsDueJob_ThroughTheLauncher()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        var launcher = new FakeLauncher((job, _) => store.EndIfRunningAsync(job.Id, JobState.Succeeded, "", Ct));
        using var worker = new QueueWorker(store, launcher, _time);

        await worker.TickAsync(Ct);
        await Task.WhenAll(worker.Running);

        Assert.Equal(["j1"], launcher.Started);
        Assert.Equal(JobState.Succeeded, (await GetAsync(store, "j1")).State);
    }

    [Fact]
    public async Task Tick_LauncherThatFailsBeforeRecording_FailsTheJob()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        await store.EnqueueAsync(Job("j2", repo: "acme/web"), Ct);
        var launcher = new FakeLauncher((job, _) => job.Id == "j1" ? throw new InvalidOperationException("No token.") : Task.CompletedTask);
        using var worker = new QueueWorker(store, launcher, _time);

        await worker.TickAsync(Ct); // One slot: j1 now, j2 once it ends.
        await Task.WhenAll(worker.Running);
        await worker.TickAsync(Ct);
        await Task.WhenAll(worker.Running);

        var j1 = await GetAsync(store, "j1");
        Assert.Equal((JobState.Failed, "No token."), (j1.State, j1.Error));
        var j2 = await GetAsync(store, "j2");
        Assert.Equal((JobState.Failed, "The job ended without recording a result."), (j2.State, j2.Error));
    }

    [Fact]
    public async Task Tick_CancelRequest_StopsTheRunningJob()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        var launcher = new FakeLauncher((_, ct) => Task.Delay(Timeout.Infinite, ct));
        using var worker = new QueueWorker(store, launcher, _time);
        await worker.TickAsync(Ct);
        await launcher.WaitStartedAsync();

        await store.CancelJobAsync("j1", Ct);
        await worker.TickAsync(Ct);
        await Task.WhenAll(worker.Running);

        Assert.Equal(JobState.Cancelled, (await GetAsync(store, "j1")).State);
    }

    [Fact]
    public async Task Run_Stopping_QueuesTheRunningJobAgain()
    {
        var store = await OpenAsync();
        await store.EnqueueAsync(Job("j1"), Ct);
        var launcher = new FakeLauncher((_, ct) => Task.Delay(Timeout.Infinite, ct));
        using var worker = new QueueWorker(store, launcher, _time);
        using var stop = new CancellationTokenSource();

        var run = worker.RunAsync(stop.Token);
        await launcher.WaitStartedAsync();
        await stop.CancelAsync();
        await run;

        var job = await GetAsync(store, "j1");
        Assert.Equal(JobState.Queued, job.State);
        Assert.Null(job.Error);
    }

    [Fact]
    public async Task Run_FailsJobsADeadProcessLeftRunning_BeforeStartingAny()
    {
        var store = await OpenAsync();
        _time.Now = Process.GetCurrentProcess().StartTime.ToUniversalTime().AddMinutes(-5); // Before this process: its runner is gone.
        await store.EnqueueAsync(Job("orphan"), Ct);
        await store.TryClaimJobAsync("orphan", Ct);
        _time.Now = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(Job("ours", repo: "acme/web"), Ct);
        await store.TryClaimJobAsync("ours", Ct);
        await store.EnqueueAsync(Job("next"), Ct);
        var launcher = new FakeLauncher((job, _) => store.EndIfRunningAsync(job.Id, JobState.Succeeded, "", Ct), slots: 2);
        using var worker = new QueueWorker(store, launcher, _time);
        using var stop = new CancellationTokenSource();

        var run = worker.RunAsync(stop.Token);
        await launcher.WaitStartedAsync();
        await stop.CancelAsync();
        await run;

        Assert.Equal(JobState.Failed, (await GetAsync(store, "orphan")).State);
        Assert.Equal(JobState.Running, (await GetAsync(store, "ours")).State);
        Assert.Equal(["next"], launcher.Started); // The orphan's PR was free again.
    }

    private sealed class FakeLauncher(Func<JobRecord, CancellationToken, Task> run, int slots = 1) : IJobLauncher
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Started { get; } = [];

        public int MaxConcurrency(string agentId) => slots;

        public Task RunAsync(JobRecord job, CancellationToken ct)
        {
            lock (Started)
            {
                Started.Add(job.Id);
            }

            _started.TrySetResult();
            return run(job, ct);
        }

        public Task WaitStartedAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
