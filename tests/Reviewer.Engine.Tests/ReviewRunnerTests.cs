using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;
using Reviewer.Git;
using Reviewer.Git.Tests;
using Reviewer.Instructions;

namespace Reviewer.Engine.Tests;

public sealed class ReviewRunnerTests : IDisposable
{
    private readonly SourceRepo _source = new();
    private readonly ReviewerPaths _paths;

    public ReviewRunnerTests() => _paths = new ReviewerPaths(_source.NewDirectory("home"));

    public void Dispose() => _source.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private static Finding Finding(string id, int line, Severity severity = Severity.Major, double confidence = 0.9) =>
        new(id, severity, Category.Correctness, confidence, "src/app.txt", line, line, $"Title {id}", "Body");

    private ReviewStore? _store;

    private async Task<(ReviewRunner Runner, FakeProvider Provider, ReviewRequest Request)> SetUpAsync(
        FindingsReport? report, bool dryRun = false, Action? onFeature = null, Exception? agentError = null)
    {
        _source.Write("src/app.txt", "one", "two");
        _source.Commit("base");
        _source.Git("switch", "--quiet", "-c", "feature");
        _source.Write("src/app.txt", "one", "  TWO  ", "three");
        onFeature?.Invoke();
        var head = _source.Commit("change");
        _source.Git("switch", "--quiet", "main");

        var pr = new PullRequestRef(new RepoRef(new Uri("https://github.com"), "acme", "app"), 7);
        var snapshot = new PullRequestSnapshot(pr, "t", "", head, _source.Head(), "main", "alice", [], [], [], false, CiState.Success, true);
        var provider = new FakeProvider(snapshot);
        var git = await GitRepository.OpenOrInitAsync(_source.NewDirectory("cache.git"), Ct);
        _store = await ReviewStore.OpenAsync(_paths.Database, TimeProvider.System, Ct);
        var runner = new ReviewRunner(
            provider, new ReviewContextBuilder(git, _paths, TimeProvider.System), new FakeAgent(report, agentError), _store);
        var request = new ReviewRequest(
            pr,
            new ReviewSource(_source.Url, "refs/heads/feature"),
            ReviewerConfig.Default.Defaults.ToReviewPolicy() with { DryRun = dryRun },
            new InstructionSettings(true, [], [], RepoAgentFileMode.Ignore),
            TimeSpan.FromMinutes(1),
            "cfg123");
        return (runner, provider, request);
    }

    [Fact]
    public async Task Run_PublishesPlan_WithFingerprints_AndRemovesWorktree()
    {
        var report = new FindingsReport("Looks risky.", [Finding("in", 2), Finding("outside", 1), Finding("weak", 3, confidence: 0.1)]);
        var (runner, provider, request) = await SetUpAsync(report);

        var outcome = await runner.RunAsync(request, Ct);

        Assert.True(outcome.Published);
        var published = Assert.Single(provider.Published);
        Assert.Equal(outcome.Snapshot.HeadSha, published.HeadSha);
        Assert.Equal(ReviewDecision.None, published.Decision);
        var comment = Assert.Single(published.Comments);
        Assert.Equal("in", comment.Finding.Id);
        Assert.Equal(FindingFingerprint.Compute(comment.Finding, "TWO"), comment.Fingerprint);
        Assert.Equal("outside", Assert.Single(published.SummaryFindings).Id);
        Assert.StartsWith("Looks risky.", published.Summary, StringComparison.Ordinal);
        Assert.Contains("with fake", published.Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outcome.AttemptFolder!, ReviewRunner.PlanFile)));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(outcome.AttemptFolder!)!, "worktree-1")));
    }

    [Fact]
    public async Task Run_FingerprintsSymlinkByItsTarget_NeverReadingThroughIt()
    {
        var secret = _source.NewDirectory("secret.txt");
        await File.WriteAllTextAsync(secret, "TOP SECRET\n", Ct);
        var finding = Finding("link", 1) with { File = "src/link.txt" };
        var (runner, provider, request) = await SetUpAsync(new FindingsReport("s", [finding]), onFeature: () =>
        {
            // A symlink blob (mode 120000) made with plumbing, so the test doesn't need symlink rights on Windows.
            var target = _source.NewDirectory("target.txt");
            File.WriteAllText(target, secret);
            var blob = _source.Git("hash-object", "-w", target).Trim();
            _source.Git("update-index", "--add", "--cacheinfo", $"120000,{blob},src/link.txt");
            _source.Git("checkout", "--", "src/link.txt");
        });

        await runner.RunAsync(request, Ct);

        var comment = Assert.Single(Assert.Single(provider.Published).Comments);
        Assert.Equal(FindingFingerprint.Compute(finding, secret), comment.Fingerprint);
        Assert.NotEqual(FindingFingerprint.Compute(finding, "TOP SECRET"), comment.Fingerprint);
    }

    [Fact]
    public async Task Run_DryRun_PublishesNothing_ButWritesPlan()
    {
        var (runner, provider, request) = await SetUpAsync(new FindingsReport("Fine.", [Finding("in", 2)]), dryRun: true);

        var outcome = await runner.RunAsync(request, Ct);

        Assert.False(outcome.Published);
        Assert.Empty(provider.Published);
        Assert.Single(outcome.Plan.InlineFindings);
        Assert.True(File.Exists(Path.Combine(outcome.AttemptFolder!, ReviewRunner.PlanFile)));
    }

    [Fact]
    public async Task Run_AgentFailure_PublishesNothing_AndRemovesWorktree()
    {
        var (runner, provider, request) = await SetUpAsync(null);

        await Assert.ThrowsAsync<AgentFailedException>(() => runner.RunAsync(request, Ct));

        Assert.Empty(provider.Published);
        var job = Assert.Single(Directory.GetDirectories(_paths.Jobs));
        Assert.False(Directory.Exists(Path.Combine(job, "worktree-1")));

        var record = (await _store!.GetJobAsync(Path.GetFileName(job), Ct))!.Job;
        Assert.Equal(JobState.Failed, record.State);
        Assert.Equal("boom", record.Error);
        Assert.NotNull(record.HeadSha);
        Assert.NotNull(record.FinishedAt);
        Assert.Null(await _store.GetPullRequestAsync(request.PullRequest, Ct));
    }

    [Fact]
    public async Task Run_RecordsJob_FindingsAndLastReviewedHead()
    {
        var report = new FindingsReport("Looks risky.", [Finding("in", 2), Finding("outside", 1), Finding("weak", 3, confidence: 0.1)]);
        var (runner, provider, request) = await SetUpAsync(report);

        var outcome = await runner.RunAsync(request, Ct);

        var details = (await _store!.GetJobAsync(outcome.JobId, Ct))!;
        Assert.Equal(JobState.Succeeded, details.Job.State);
        Assert.True(details.Job.Published);
        Assert.Equal("cfg123", details.Job.ConfigHash);
        Assert.Equal("fake", details.Job.AgentId);
        Assert.Equal(outcome.Snapshot.HeadSha, details.Job.HeadSha);
        Assert.Equal(outcome.AttemptFolder, details.Job.AttemptFolder);
        Assert.Equal("Looks risky.", details.Job.Summary);
        Assert.Equal(
            [("in", FindingDisposition.Inline), ("outside", FindingDisposition.Summary), ("weak", FindingDisposition.Dropped)],
            details.Findings.Select(f => (f.FindingId, f.Disposition)));
        Assert.True(details.Findings[0].Blocking);
        Assert.Equal(Assert.Single(Assert.Single(provider.Published).Comments).Fingerprint, details.Findings[0].Fingerprint);
        Assert.NotNull(details.Findings[1].Fingerprint);
        Assert.Null(details.Findings[2].Fingerprint);

        var pr = (await _store.GetPullRequestAsync(request.PullRequest, Ct))!;
        Assert.Equal(outcome.Snapshot.HeadSha, pr.LastReviewedHead);
        Assert.Equal(outcome.Snapshot.BaseSha, pr.LastReviewedBase);
        Assert.Equal(outcome.JobId, pr.LastJobId);
    }

    [Fact]
    public async Task Run_DryRun_IsRecorded_ButDoesNotCountAsReviewed()
    {
        var (runner, _, request) = await SetUpAsync(new FindingsReport("Fine.", [Finding("in", 2)]), dryRun: true);

        var outcome = await runner.RunAsync(request, Ct);

        var job = (await _store!.GetJobAsync(outcome.JobId, Ct))!.Job;
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.True(job.DryRun);
        Assert.False(job.Published);
        Assert.Null(await _store.GetPullRequestAsync(request.PullRequest, Ct));
    }

    [Fact]
    public async Task Run_Throttled_RecordsRetryTime()
    {
        var retryAt = new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);
        var (runner, _, request) = await SetUpAsync(null, agentError: new AgentThrottledException("Usage limit reached.", retryAt));

        await Assert.ThrowsAsync<AgentThrottledException>(() => runner.RunAsync(request, Ct));

        var job = Assert.Single(await _store!.ListJobsAsync(null, 10, Ct)).Job;
        Assert.Equal(JobState.Throttled, job.State);
        Assert.Equal(retryAt, job.RetryAt);
    }

    private sealed class FakeAgent(FindingsReport? report, Exception? error) : IReviewAgent
    {
        public string Id => "fake";

        public AgentCapabilities Capabilities { get; } = new(true, 1, true);

        public Task<AgentHealth> CheckAsync(CancellationToken ct) => Task.FromResult(new AgentHealth(true, true, "1"));

        public Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct)
        {
            Assert.True(File.Exists(Path.Combine(context.AttemptFolder, ReviewContextBuilder.DeltaFile)));
            return report is null ? throw error ?? new AgentFailedException("boom") : Task.FromResult(report);
        }
    }
}
