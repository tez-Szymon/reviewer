using Reviewer.Core.Providers;
using Reviewer.Engine.Config;

namespace Reviewer.Engine.Tests;

public sealed class TriggerRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly TriggerConfig Defaults = ReviewerConfig.Default.Defaults.Triggers;

    private static PullRequestSummary Pr(
        string head = "h1",
        string author = "alice",
        string[]? assignees = null,
        string[]? reviewers = null,
        string[]? labels = null,
        bool draft = false,
        string baseBranch = "main") =>
        new(new PullRequestRef(new RepoRef(new Uri("https://github.com"), "acme", "api"), 7), head, baseBranch, author,
            assignees ?? [], reviewers ?? [], labels ?? [], draft);

    private static SeenPullRequest Seen(string head = "h1", bool matched = true, bool assigned = false, bool requested = true) =>
        new(head, matched, assigned, requested);

    [Fact]
    public void PassesFilters_SkipsDraftsAndOtherTargetBranches()
    {
        Assert.True(TriggerRules.PassesFilters(Pr(), Defaults));
        Assert.False(TriggerRules.PassesFilters(Pr(draft: true), Defaults));
        Assert.True(TriggerRules.PassesFilters(Pr(draft: true), Defaults with { SkipDrafts = false }));

        var mainOnly = Defaults with { TargetBranches = ["main"] };
        Assert.True(TriggerRules.PassesFilters(Pr(), mainOnly));
        Assert.False(TriggerRules.PassesFilters(Pr(baseBranch: "release"), mainOnly));
    }

    [Theory]
    [InlineData(InitialTrigger.All, true)]
    [InlineData(InitialTrigger.AssignedToMe, true)]
    [InlineData(InitialTrigger.ReviewRequested, false)]
    [InlineData(InitialTrigger.HasLabel, true)]
    [InlineData(InitialTrigger.AuthoredBy, false)]
    public void See_MatchesEachInitialTrigger(InitialTrigger trigger, bool expected)
    {
        var triggers = Defaults with { Initial = [trigger], Labels = ["AI-Review"], Authors = ["carol"] };
        var pr = Pr(assignees: ["BOB"], reviewers: ["dave"], labels: ["ai-review"]);

        var seen = TriggerRules.See(pr, triggers, "bob");

        Assert.Equal(expected, seen.Matched);
        Assert.True(seen.Assigned);
        Assert.False(seen.ReviewRequested);
    }

    [Fact]
    public void Decide_NotFollowed_QueuesWhenAnInitialTriggerStartsToMatch()
    {
        var matching = Seen();

        Assert.Equal([TriggerReason.Initial], TriggerRules.Decide(matching, null, null, Defaults, Now)!.Reasons);
        Assert.Null(TriggerRules.Decide(matching, null, null, Defaults, Now)!.NotBefore);
        Assert.NotNull(TriggerRules.Decide(matching, Seen(matched: false, requested: false), null, Defaults, Now));
        Assert.Null(TriggerRules.Decide(matching, matching, null, Defaults, Now));
        Assert.Null(TriggerRules.Decide(Seen(matched: false, requested: false), null, null, Defaults, Now));
    }

    [Fact]
    public void Decide_NotFollowed_ChecksAgainOnAPush()
    {
        // Turned away for size or paths, or its review failed: a push gives it another chance.
        Assert.NotNull(TriggerRules.Decide(Seen("h2"), Seen("h1"), null, Defaults, Now));
    }

    [Fact]
    public void Decide_NewCommits_WaitsTheDebounce()
    {
        var decision = TriggerRules.Decide(Seen("h2", requested: false), Seen("h1", requested: false), "h1", Defaults, Now);

        Assert.Equal([TriggerReason.NewCommits], decision!.Reasons);
        Assert.Equal(Now.AddMinutes(5), decision.NotBefore);
    }

    [Fact]
    public void Decide_Followed_IgnoresInitialTriggers()
    {
        // GitHub drops the review request once the review is posted; the PR is still followed.
        var unrequested = Seen("h2", matched: false, requested: false);

        Assert.NotNull(TriggerRules.Decide(unrequested, Seen("h1"), "h1", Defaults, Now));
        Assert.Null(TriggerRules.Decide(unrequested, Seen("h2"), "h1", Defaults, Now));
    }

    [Fact]
    public void Decide_FollowedButUnseen_ComparesWithTheReviewedHead()
    {
        Assert.Equal([TriggerReason.NewCommits], TriggerRules.Decide(Seen("h2", assigned: true), null, "h1", Defaults, Now)!.Reasons);
        // Already assigned and requested, but not seen being so: no telling it's new.
        Assert.Null(TriggerRules.Decide(Seen("h1", assigned: true), null, "h1", Defaults, Now));
    }

    [Fact]
    public void Decide_AssignmentOrRequest_StartsAtOnce_EvenWithNewCommits()
    {
        var before = Seen("h1", assigned: false, requested: false);

        Assert.Equal([TriggerReason.Assigned], TriggerRules.Decide(Seen("h1", assigned: true, requested: false), before, "h1", Defaults, Now)!.Reasons);
        Assert.Equal([TriggerReason.ReviewRequested], TriggerRules.Decide(Seen("h1", requested: true), before, "h1", Defaults, Now)!.Reasons);

        var both = TriggerRules.Decide(Seen("h2", requested: true), before, "h1", Defaults, Now);
        Assert.Equal([TriggerReason.NewCommits, TriggerReason.ReviewRequested], both!.Reasons);
        Assert.Null(both.NotBefore);
    }

    [Fact]
    public void Decide_ReReviewTriggersCanBeTurnedOff()
    {
        var off = Defaults with { ReReview = new ReReviewConfig(false, 5, false, false) };
        var before = Seen("h1", assigned: false, requested: false);

        Assert.Null(TriggerRules.Decide(Seen("h2", assigned: true, requested: true), before, "h1", off, Now));
    }

    [Fact]
    public void RejectChanges_AppliesMaxDiffLinesAndPaths()
    {
        ChangedFile[] files = [new("src/Api/Orders.cs", 1500, 200), new("README.md", 1, 1)];

        Assert.Null(TriggerRules.RejectChanges(files, Defaults));
        Assert.Contains("1702 lines", TriggerRules.RejectChanges(files, Defaults with { MaxDiffLines = 1000 }), StringComparison.Ordinal);
        Assert.Null(TriggerRules.RejectChanges(files, Defaults with { Paths = ["src/**/*.cs"] }));
        Assert.NotNull(TriggerRules.RejectChanges(files, Defaults with { Paths = ["docs/**"] }));
    }
}
