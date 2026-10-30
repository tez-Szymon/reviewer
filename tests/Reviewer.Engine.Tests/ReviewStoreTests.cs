using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Engine.State;

namespace Reviewer.Engine.Tests;

public sealed class ReviewStoreTests : IDisposable
{
    private static readonly PullRequestRef Pr = new(new RepoRef(new Uri("https://github.com"), "acme", "api"), 7);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-store-");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Delete(recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    private string DbPath => Path.Combine(_dir.FullName, "data", ReviewStore.FileName);

    private static JobRecord Job(string id, string repo = "acme/api", int number = 7) => new()
    {
        Id = id, Host = "https://github.com", Repo = repo, Number = number, ConfigHash = "h", AgentId = "claude-code", AppVersion = "test",
    };

    private static ReviewOutcome Outcome(string jobId, string head, bool published, params Finding[] findings)
    {
        var snapshot = new PullRequestSnapshot(Pr, "t", "", head, "base1", "main", "alice", [], [], [], false, CiState.Success, true);
        var plan = new ReviewPlan("s", findings.Take(1).ToList(), findings.Skip(1).Take(1).ToList(), [.. findings.Take(1).Select(f => f.Id)], [],
            ReviewDecision.Approve, Merge: false, UnresolvedThreads: false, []);
        return new ReviewOutcome(
            jobId, "/tmp/attempt", snapshot, new ReviewDelta(DeltaKind.Full, "base1", null, "First."), new FindingsReport("Summary.", findings), plan, published);
    }

    private static readonly ReviewedState State = new("head", ["p1"], new Dictionary<string, string> { ["a.cs"] = "f1" });

    private static Finding Finding(string id) => new(id, Severity.Major, Category.Correctness, 0.9, "a.cs", 1, 2, "T", "B");

    [Fact]
    public async Task Open_CreatesDatabase_AndReopensIt()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        await store.StartJobAsync(Job("j1"), Ct);

        var reopened = await ReviewStore.OpenAsync(DbPath, _time, Ct);

        Assert.True(File.Exists(DbPath));
        Assert.Single(await reopened.ListJobsAsync(null, 10, Ct));
    }

    [Fact]
    public async Task StartJob_TakenId_GetsASuffix()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);

        Assert.Equal("j1", await store.StartJobAsync(Job("j1"), Ct));
        Assert.Equal("j1-2", await store.StartJobAsync(Job("j1"), Ct));
        Assert.Equal("j1-3", await store.StartJobAsync(Job("j1"), Ct));
    }

    [Fact]
    public async Task CompleteJob_StoresFindings_AndUpdatesTheSamePullRequestRow()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        var first = await store.StartJobAsync(Job("j1"), Ct);
        var a = Finding("a");
        var b = Finding("b") with { File = "b.cs" };
        await store.CompleteJobAsync(first, Outcome(first, "head1", published: true, a, b, Finding("c")), new Dictionary<Finding, string> { [a] = "fpa", [b] = "fpb" }, State, [], Ct);

        _time.Now = _time.Now.AddHours(1);
        var second = await store.StartJobAsync(Job("j2"), Ct);
        await store.CompleteJobAsync(second, Outcome(second, "head2", published: true), new Dictionary<Finding, string>(), State, [], Ct);

        var details = (await store.GetJobAsync(first, Ct))!;
        Assert.Equal(ReviewDecision.Approve, details.Job.Decision);
        Assert.Equal(["fpa", "fpb", null], details.Findings.Select(f => f.Fingerprint));
        Assert.Equal([true, false, false], details.Findings.Select(f => f.Blocking));

        var pr = (await store.GetPullRequestAsync(Pr with { Repo = Pr.Repo with { Owner = "ACME" } }, Ct))!;
        Assert.Equal("head2", pr.LastReviewedHead);
        Assert.Equal(second, pr.LastJobId);
        Assert.Equal(_time.Now, pr.LastReviewedAt);
    }

    [Fact]
    public async Task FailJob_RecordsStateErrorAndRetryTime()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        var id = await store.StartJobAsync(Job("j1"), Ct);
        var retry = _time.Now.AddHours(5);

        await store.FailJobAsync(id, JobState.Throttled, "limit", retry, Ct);

        var job = (await store.GetJobAsync(id, Ct))!.Job;
        Assert.Equal(JobState.Throttled, job.State);
        Assert.Equal("limit", job.Error);
        Assert.Equal(retry, job.RetryAt);
        Assert.Equal(_time.Now, job.FinishedAt);
    }

    [Fact]
    public async Task ListJobs_NewestFirst_FilteredByRepoIgnoringCase_WithCounts()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        foreach (var (id, repo) in new[] { ("j1", "acme/api"), ("j2", "acme/web"), ("j3", "acme/api") })
        {
            await store.StartJobAsync(Job(id, repo), Ct);
            _time.Now = _time.Now.AddMinutes(1);
        }

        var a = Finding("a");
        await store.CompleteJobAsync("j3", Outcome("j3", "h", false, a, Finding("b"), Finding("c")), new Dictionary<Finding, string> { [a] = "fp" }, State, [], Ct);

        var jobs = await store.ListJobsAsync("Acme/API", 10, Ct);

        Assert.Equal(["j3", "j1"], jobs.Select(j => j.Job.Id));
        Assert.Equal((1, 1, 1), (jobs[0].Inline, jobs[0].SummaryOnly, jobs[0].Blocking));
        Assert.Single(await store.ListJobsAsync(null, 1, Ct));
        Assert.Empty(await store.ListJobsAsync("acme_api", 10, Ct));
    }

    [Fact]
    public async Task GetJob_MatchesUniquePrefix_AndRejectsAmbiguousOnes()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        await store.StartJobAsync(Job("20261030-120000-acme-api-7"), Ct);
        await store.StartJobAsync(Job("20261030-130000-acme-api-8"), Ct);

        Assert.Equal("20261030-130000-acme-api-8", (await store.GetJobAsync("20261030-13", Ct))!.Job.Id);
        Assert.Null(await store.GetJobAsync("nope", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetJobAsync("20261030", Ct));
    }

[Fact]
    public async Task Open_UpgradesADatabaseFromBeforeDeltaReview_KeepingItsRows()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await using (var db = new ReviewerDb(ReviewerDb.Options(DbPath)))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261030124209_Initial", Ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Jobs (Id, Host, Repo, Number, ConfigHash, AgentId, AppVersion, State, DryRun, Published, CreatedAt)
                VALUES ('j1', 'https://github.com', 'acme/api', 7, 'h', 'claude-code', 'old', 'Succeeded', 0, 1, 0);
                INSERT INTO PullRequests (Host, Repo, Number, LastReviewedHead, LastReviewedBase, LastReviewedAt, LastJobId)
                VALUES ('https://github.com', 'acme/api', 7, 'head1', 'base1', 0, 'j1');
                """, Ct);
        }

        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        var previous = (await store.GetPreviousReviewAsync(Pr, Ct))!;

        Assert.Equal("head1", previous.State.HeadSha);
        Assert.Equal("h", previous.ConfigHash);
        Assert.Empty(previous.State.CommitPatchIds);
        Assert.Empty(previous.State.FilePatchIds);
    }

    [Fact]
    public async Task OpenFindings_ArePostedOnesOfPublishedJobs_UntilResolved()
    {
        var store = await ReviewStore.OpenAsync(DbPath, _time, Ct);
        var a = Finding("a");
        var b = Finding("b") with { File = "b.cs" };
        var first = await store.StartJobAsync(Job("j1"), Ct);
        await store.CompleteJobAsync(first, Outcome(first, "head1", published: true, a, b, Finding("dropped")), new Dictionary<Finding, string> { [a] = "fpa", [b] = "fpb" }, State, [], Ct);
        var dry = await store.StartJobAsync(Job("j2"), Ct);
        var c = Finding("c");
        await store.CompleteJobAsync(dry, Outcome(dry, "head2", published: false, c), new Dictionary<Finding, string> { [c] = "fpc" }, State, [], Ct);

        var open = await store.GetOpenFindingsAsync(Pr, Ct);
        Assert.Equal(["fpa", "fpb"], open.Select(o => o.Fingerprint));
        Assert.Equal(("head1", true), (open[0].HeadSha, open[0].Blocking));

        var third = await store.StartJobAsync(Job("j3"), Ct);
        await store.CompleteJobAsync(third, Outcome(third, "head3", published: true), new Dictionary<Finding, string>(), State, ["fpa"], Ct);

        Assert.Equal("fpb", Assert.Single(await store.GetOpenFindingsAsync(Pr, Ct)).Fingerprint);
        Assert.Equal(["p1"], (await store.GetPreviousReviewAsync(Pr, Ct))!.State.CommitPatchIds);
        Assert.Equal("f1", (await store.GetPreviousReviewAsync(Pr, Ct))!.State.FilePatchIds["a.cs"]);
    }

        private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
