using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;
using Reviewer.Git;
using Reviewer.Git.Tests;
using Reviewer.Instructions;

namespace Reviewer.Engine.Tests;

/// <summary>Reviewing the same PR again: deltas, skips, dedup, and earlier blockers carried or resolved.</summary>
public sealed class ReReviewTests : IDisposable
{
    private static readonly PullRequestRef Pr = new(new RepoRef(new Uri("https://github.com"), "acme", "app"), 7);

    private readonly SourceRepo _source = new();
    private readonly ScriptedAgent _agent = new();
    private FakeProvider _provider = null!;
    private ReviewStore _store = null!;
    private ReviewRunner _runner = null!;

    public ReReviewTests()
    {
        _source.Write("src/app.txt", [.. Enumerable.Range(1, 10).Select(i => $"line {i}")]);
        _source.Commit("base");
        _source.Git("switch", "--quiet", "-c", "feature");
        Edit(3, "BUG");
        _source.Commit("introduce a bug");
    }

    public void Dispose() => _source.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    // A blocker on line 3, where "BUG" is.
    private static Finding Bug => new("bug", Severity.Major, Category.Correctness, 0.9, "src/app.txt", 3, 3, "Bug", "Broken.");

    private void Edit(int line, string text)
    {
        var path = Path.Combine(_source.Path, "src", "app.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        lines[line - 1] = text;
        _source.Write("src/app.txt", lines);
    }

    private async Task<ReviewOutcome> ReviewAsync(bool full = false, string config = "cfg", bool dryRun = false)
    {
        var snapshot = new PullRequestSnapshot(
            Pr, "t", "", _source.Git("rev-parse", "feature").Trim(), _source.Git("rev-parse", "main").Trim(), "main", "alice",
            [], [], [], false, CiState.Success, true);
        if (_runner is null)
        {
            var paths = new ReviewerPaths(_source.NewDirectory("home"));
            var git = await GitRepository.OpenOrInitAsync(_source.NewDirectory("cache.git"), Ct);
            _provider = new FakeProvider(snapshot);
            _store = await ReviewStore.OpenAsync(paths.Database, TimeProvider.System, Ct);
            _runner = new ReviewRunner(_provider, new ReviewContextBuilder(git, paths, TimeProvider.System), _agent, _store);
        }

        _provider.Snapshot = snapshot;
        var request = new ReviewRequest(
            Pr,
            new ReviewSource(_source.Url, "refs/heads/feature"),
            ReviewerConfig.Default.Defaults.ToReviewPolicy() with { Mode = ReviewMode.Gate, DryRun = dryRun },
            new InstructionSettings(true, [], [], RepoAgentFileMode.Ignore),
            TimeSpan.FromMinutes(1),
            config,
            full);
        return await _runner.RunAsync(request, Ct);
    }

    [Fact]
    public async Task SameHead_IsSkipped_WithoutRunningTheAgent()
    {
        _agent.Reports.Enqueue(new FindingsReport("ok", []));
        await ReviewAsync();

        var again = await ReviewAsync();

        Assert.True(again.Skipped);
        Assert.Equal(DeltaKind.Unchanged, again.Delta.Kind);
        Assert.Single(_agent.Contexts);
        Assert.Single(_provider.Published);
        Assert.Equal(JobState.Skipped, (await _store.GetJobAsync(again.JobId, Ct))!.Job.State);
    }

    [Fact]
    public async Task SameHead_RunsAgain_WithFull_OrANewConfig()
    {
        _agent.Reports.Enqueue(new FindingsReport("ok", []));
        _agent.Reports.Enqueue(new FindingsReport("ok", []));
        _agent.Reports.Enqueue(new FindingsReport("ok", []));
        await ReviewAsync();

        var full = await ReviewAsync(full: true);
        var reconfigured = await ReviewAsync(config: "cfg2");

        Assert.Equal(DeltaKind.Full, full.Delta.Kind);
        Assert.Equal(DeltaKind.Full, reconfigured.Delta.Kind);
        Assert.Equal(3, _agent.Contexts.Count);
    }

    [Fact]
    public async Task Push_ReviewsOnlyTheNewCommit()
    {
        _agent.Reports.Enqueue(new FindingsReport("ok", []));
        await ReviewAsync();
        Edit(9, "later");
        _source.Commit("later change");
        _agent.Reports.Enqueue(new FindingsReport("ok", []));

        var outcome = await ReviewAsync();

        Assert.Equal(DeltaKind.NewCommits, outcome.Delta.Kind);
        var patch = await File.ReadAllTextAsync(Path.Combine(_agent.Contexts[1].AttemptFolder, ReviewContextBuilder.DeltaFile), Ct);
        Assert.Contains("+later", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("BUG", patch, StringComparison.Ordinal);
        Assert.Contains("commits pushed since", _provider.Published[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocker_LeftAlone_KeepsBlocking_AndIsNotPostedAgain()
    {
        _agent.Reports.Enqueue(new FindingsReport("bug", [Bug]));
        var first = await ReviewAsync();
        Edit(9, "unrelated");
        _source.Commit("unrelated change");
        _agent.Reports.Enqueue(new FindingsReport("fine", []));

        var second = await ReviewAsync();

        Assert.Equal(ReviewDecision.RequestChanges, first.Plan.Decision);
        Assert.Equal(ReviewDecision.RequestChanges, second.Plan.Decision);
        Assert.Equal(1, second.StillOpen);
        Assert.Equal(0, second.Resolved);
        Assert.Empty(_provider.Published[1].Comments);
        Assert.Contains("1 earlier finding(s) still open, 1 blocking", _provider.Published[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocker_ReportedAgain_IsNotPostedTwice_AndStillBlocks()
    {
        _agent.Reports.Enqueue(new FindingsReport("bug", [Bug]));
        await ReviewAsync();
        Edit(9, "unrelated");
        _source.Commit("unrelated change");
        _agent.Reports.Enqueue(new FindingsReport("still a bug", [Bug with { Id = "again" }]));

        var second = await ReviewAsync();

        Assert.Equal(ReviewDecision.RequestChanges, second.Plan.Decision);
        Assert.Equal("again", Assert.Single(second.Plan.AlreadyPosted).Id);
        Assert.Empty(_provider.Published[1].Comments);
        var stored = (await _store.GetJobAsync(second.JobId, Ct))!.Findings;
        Assert.Equal(FindingDisposition.AlreadyPosted, Assert.Single(stored).Disposition);
    }

    [Fact]
    public async Task Blocker_WhoseLinesChanged_AndNotReportedAgain_IsResolved()
    {
        _agent.Reports.Enqueue(new FindingsReport("bug", [Bug]));
        var first = await ReviewAsync();
        Edit(3, "fixed");
        _source.Commit("fix the bug");
        _agent.Reports.Enqueue(new FindingsReport("fixed", []));

        var second = await ReviewAsync();

        Assert.Equal(ReviewDecision.Approve, second.Plan.Decision);
        Assert.Equal(1, second.Resolved);
        Assert.Empty(await _store.GetOpenFindingsAsync(Pr, Ct));
        Assert.Equal(second.JobId, (await _store.GetJobAsync(first.JobId, Ct))!.Findings.Single().ResolvedByJobId);
        Assert.Contains("1 earlier finding(s) fixed", _provider.Published[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRun_ShowsTheResolution_ButNeitherRecordsIt_NorMovesTheReviewedHead()
    {
        _agent.Reports.Enqueue(new FindingsReport("bug", [Bug]));
        var first = await ReviewAsync();
        Edit(3, "fixed");
        _source.Commit("fix the bug");
        _agent.Reports.Enqueue(new FindingsReport("fixed", []));
        _agent.Reports.Enqueue(new FindingsReport("fixed", []));

        var dry = await ReviewAsync(dryRun: true);
        var real = await ReviewAsync();

        Assert.Equal(1, dry.Resolved);
        Assert.False(dry.Published);
        Assert.Equal(DeltaKind.NewCommits, real.Delta.Kind); // Not skipped: the dry run didn't count as reviewed.
        Assert.Equal(first.Snapshot.HeadSha, real.Delta.FromSha);
        Assert.Equal(1, real.Resolved);
    }

    private sealed class ScriptedAgent : IReviewAgent
    {
        public Queue<FindingsReport> Reports { get; } = new();

        public List<ReviewContext> Contexts { get; } = [];

        public string Id => "fake";

        public AgentCapabilities Capabilities { get; } = new(true, 1, true);

        public Task<AgentHealth> CheckAsync(CancellationToken ct) => Task.FromResult(new AgentHealth(true, true, "1"));

        public Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct)
        {
            Contexts.Add(context);
            return Task.FromResult(Reports.Dequeue());
        }
    }
}
