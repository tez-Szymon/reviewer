using Reviewer.Api;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.Tests;

public class ViewModelTests
{
    private readonly FakeEngine _engine = new();
    private readonly FakeShell _shell = new();

    private static JobDto Job(string id, JobState state, long version, int number = 7) => FakeEngine.Job(id, state, version, number);

    [Fact]
    public async Task Queue_LoadsOldestFirst_AndDropsJobsThatFinish()
    {
        var first = Job("a", JobState.Running, 1);
        var second = Job("b", JobState.Queued, 2);
        _engine.Queue.AddRange([second, first]);
        var queue = new QueueViewModel(_engine, _shell);

        await queue.LoadAsync();
        queue.OnChanged(first with { State = JobState.Succeeded, Version = 3 });

        Assert.Equal(["b"], queue.Jobs.Select(j => j.Id));
        queue.OnChanged(Job("c", JobState.Queued, 4));
        Assert.Equal(["b", "c"], queue.Jobs.Select(j => j.Id));
    }

    [Fact]
    public async Task Queue_KeepsEventsThatArriveWhileItLoads()
    {
        var job = Job("a", JobState.Queued, 1);
        _engine.Queue.Add(job);
        var queue = new QueueViewModel(_engine, _shell);
        _engine.DuringLoad = () => queue.OnChanged(job with { State = JobState.Running, Version = 2 });

        await queue.LoadAsync();

        Assert.Equal(JobState.Running, Assert.Single(queue.Jobs).Job.State);
    }

    [Fact]
    public async Task Queue_IgnoresOlderVersions()
    {
        var job = Job("a", JobState.Running, 5);
        _engine.Queue.Add(job);
        var queue = new QueueViewModel(_engine, _shell);
        await queue.LoadAsync();

        queue.OnChanged(job with { State = JobState.Queued, Version = 4 });

        Assert.Equal(JobState.Running, Assert.Single(queue.Jobs).Job.State);
    }

    [Fact]
    public async Task Queue_ShowsWhyAnActionFailed()
    {
        _engine.Queue.Add(Job("gone", JobState.Queued, 1));
        var queue = new QueueViewModel(_engine, _shell);
        await queue.LoadAsync();

        await queue.CancelCommand.ExecuteAsync(queue.Jobs[0]);

        Assert.Equal("gone is succeeded; there's nothing to cancel.", queue.Error);
    }

    [Theory]
    [InlineData(JobState.Queued, true, false)]
    [InlineData(JobState.Throttled, true, true)]
    [InlineData(JobState.Failed, false, true)]
    [InlineData(JobState.Succeeded, false, false)]
    public void JobItem_OffersWhatTheEngineAllows(JobState state, bool cancel, bool retry)
    {
        var item = new JobItem(Job("a", state, 1));

        Assert.Equal((cancel, retry), (item.CanCancel, item.CanRetry));
    }

    [Fact]
    public async Task History_FiltersByRepoAndOutcome_AndAddsNewJobsOnTop()
    {
        var approved = Job("a", JobState.Succeeded, 1) with { Decision = ReviewDecision.Approve };
        var failed = FakeEngine.Job("b", JobState.Failed, 2, repo: "acme/web");
        _engine.Jobs.AddRange([new JobSummaryDto(failed, 0, 0, 0), new JobSummaryDto(approved, 2, 1, 0)]);
        var history = new HistoryViewModel(_engine, _shell);
        await history.LoadAsync();
        Assert.Equal(["b", "a"], history.Jobs.Select(j => j.Id));
        Assert.Equal("2 inline, 1 in summary", history.Jobs[1].CountsText);

        history.RepoFilter = "WEB";
        Assert.Equal(["b"], history.Jobs.Select(j => j.Id));

        history.RepoFilter = "";
        history.Outcome = OutcomeFilter.Approved;
        Assert.Equal(["a"], history.Jobs.Select(j => j.Id));

        history.OnChanged(Job("c", JobState.Succeeded, 3) with { Decision = ReviewDecision.Approve });
        Assert.Equal(["c", "a"], history.Jobs.Select(j => j.Id));
    }

    [Fact]
    public void Describe_SaysWhatTheReviewDid()
    {
        var merged = Job("a", JobState.Succeeded, 1) with { Decision = ReviewDecision.Approve, Merge = MergeIntent.MergeWhenGreen };
        var dryRun = Job("b", JobState.Succeeded, 1) with { Published = false, Decision = ReviewDecision.RequestChanges };

        Assert.Equal("approved, merging when CI passes", Describe.State(merged));
        Assert.Equal("dry run (requested changes)", Describe.State(dryRun));
        Assert.Equal("stopping", Describe.State(Job("c", JobState.Running, 1) with { CancelRequested = true }));
    }

    [Fact]
    public async Task Pulls_SayWhereReviewsStand_AndOpenTheLatestJob()
    {
        var latest = new JobSummaryDto(Job("j9", JobState.Succeeded, 9), 0, 0, 0);
        _engine.Pulls.AddRange([
            Pull(7, "aaaaaaa1", new LastReviewDto("bbbbbbb2", "base", DateTimeOffset.UnixEpoch, "j9"), latest),
            Pull(8, "ccccccc3", null, null, matched: false),
        ]);
        var pulls = new PullsViewModel(_engine, _shell);

        await pulls.LoadAsync();
        pulls.OpenCommand.Execute(pulls.Pulls[0]);

        Assert.Equal("reviewed bbbbbbb, new commits since", pulls.Pulls[0].ReviewText);
        Assert.Equal("not followed: no trigger matched", pulls.Pulls[1].ReviewText);
        Assert.Equal(["open j9"], _shell.Calls);
    }

    [Fact]
    public async Task JobDetail_LoadsFindingsAndReasons_AndLinksThePr()
    {
        var job = Job("j1", JobState.Succeeded, 1) with { AttemptFolder = "/data/jobs/j1/attempt-1" };
        var finding = new FindingDto("f1", Core.Findings.Severity.Major, Core.Findings.Category.Correctness, 0.9, "src/a.cs", 3, 5, "Off by one", "Body",
            null, FindingDisposition.Inline, true, "fp", null);
        _engine.Details["j1"] = new JobDetailsDto(job, [finding], ["No blocking findings.", "Mode Gate: Approve."]);
        var detail = new JobDetailViewModel(_engine, _shell, "j1");

        await detail.LoadAsync();
        await detail.OpenPullRequestCommand.ExecuteAsync(null);
        await detail.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(["No blocking findings.", "Mode Gate: Approve."], detail.Reasons);
        var item = Assert.Single(detail.Findings);
        Assert.Equal(("src/a.cs:3-5", "inline comment", true), (item.Location, item.Disposition, item.Blocking));
        Assert.Equal(["uri https://github.com/acme/api/pull/7", "folder /data/jobs/j1/attempt-1"], _shell.Calls);
    }

    [Fact]
    public async Task JobDetail_ForAnUnknownJob_SaysSo()
    {
        var detail = new JobDetailViewModel(_engine, _shell, "nope");

        await detail.LoadAsync();

        Assert.Equal("There's no job nope.", detail.Error);
        Assert.False(detail.OpenFolderCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("https://github.com", "github", "https://github.com", "https://github.com/acme/api/pull/7")]
    [InlineData("https://git.acme.test", "gitlab", "https://git.acme.test/gitlab", "https://git.acme.test/gitlab/acme/api/-/merge_requests/7")]
    [InlineData("https://elsewhere.test", "github", "https://github.com", null)]
    public void PrLink_ComesFromTheAccountForTheHost(string jobHost, string provider, string accountHost, string? expected)
    {
        var job = Job("j", JobState.Succeeded, 1) with { Host = jobHost };

        var link = JobDetailViewModel.Link(job, [new AccountDto("a", provider, new Uri(accountHost), null)]);

        Assert.Equal(expected, link?.AbsoluteUri);
    }

    private static WatchedPullRequestDto Pull(int number, string head, LastReviewDto? last, JobSummaryDto? latest, bool matched = true) =>
        new("https://github.com", "acme/api", number, $"PR {number}", "alice", head, matched, false, false, DateTimeOffset.UnixEpoch, last, latest);

    private sealed class FakeShell : IShell
    {
        public List<string> Calls { get; } = [];

        public Task OpenUriAsync(Uri uri)
        {
            Calls.Add($"uri {uri.AbsoluteUri}");
            return Task.CompletedTask;
        }

        public Task OpenFolderAsync(string path)
        {
            Calls.Add($"folder {path}");
            return Task.CompletedTask;
        }

        public void OpenJob(string id) => Calls.Add($"open {id}");

        public void Back() => Calls.Add("back");
    }
}
