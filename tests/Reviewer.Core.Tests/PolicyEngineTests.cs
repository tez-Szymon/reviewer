using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;

namespace Reviewer.Core.Tests;

public class PolicyEngineTests
{
    private const string Head = "abc123";

    private static readonly ProviderCapabilities GitHub = new(true, true, true, false);
    private static readonly ProviderCapabilities NoRequestChanges = GitHub with { SupportsRequestChanges = false };

    private static readonly ReviewPolicy Policy = new(
        Mode: ReviewMode.Gate,
        DryRun: false,
        MinConfidence: 0.6,
        BlockingRules:
        [
            new(Severity.Major, [Category.Security, Category.Correctness]),
            new(Severity.Blocker),
        ],
        IgnorePaths: ["**/*.lock", "**/dist/**"],
        MaxInlineComments: 20);

    private static readonly PullRequestSnapshot Snapshot = new(
        new PullRequestRef(new RepoRef(new Uri("https://github.com"), "acme", "api"), 7),
        Title: "Change",
        Description: "",
        HeadSha: Head,
        BaseSha: "base000",
        BaseBranch: "main",
        Author: "alice",
        Assignees: [],
        RequestedReviewers: ["reviewer-bot"],
        Labels: [],
        IsDraft: false,
        Ci: CiState.Success,
        IsMergeable: true);

    private static readonly ReviewScope Scope = new(
        Head,
        "reviewer-bot",
        new Dictionary<string, IReadOnlyList<LineRange>>
        {
            ["src/Foo.cs"] = [new(1, 100)],
            ["src/Bar.cs"] = [new(10, 20)],
            ["src/Renamed.cs"] = [],
            ["yarn.lock"] = [new(1, 500)],
            ["web/dist/app.js"] = [new(1, 500)],
        });

    private static Finding F(
        string id,
        Severity severity = Severity.Minor,
        Category category = Category.Maintainability,
        double confidence = 0.9,
        string file = "src/Foo.cs",
        int line = 10) =>
        new(id, severity, category, confidence, file, line, line, $"Title {id}", $"Body {id}");

    private static ReviewPlan Decide(
        IReadOnlyList<Finding> findings,
        ReviewPolicy? policy = null,
        PullRequestSnapshot? snapshot = null,
        ProviderCapabilities? capabilities = null,
        ReviewScope? scope = null) =>
        PolicyEngine.Decide(
            new FindingsReport("summary", findings),
            snapshot ?? Snapshot,
            policy ?? Policy,
            capabilities ?? GitHub,
            scope ?? Scope);

    private static Finding Blocker => F("b1", Severity.Major, Category.Security);

    [Theory]
    [InlineData(ReviewMode.Comment, false, ReviewDecision.None)]
    [InlineData(ReviewMode.Comment, true, ReviewDecision.None)]
    [InlineData(ReviewMode.Approve, false, ReviewDecision.Approve)]
    [InlineData(ReviewMode.Approve, true, ReviewDecision.None)]
    [InlineData(ReviewMode.Gate, false, ReviewDecision.Approve)]
    [InlineData(ReviewMode.Gate, true, ReviewDecision.RequestChanges)]
    [InlineData(ReviewMode.GateMerge, false, ReviewDecision.Approve)]
    [InlineData(ReviewMode.GateMerge, true, ReviewDecision.RequestChanges)]
    public void Decide_FollowsModeTable(ReviewMode mode, bool hasBlocking, ReviewDecision expected)
    {
        Finding[] findings = hasBlocking ? [F("n1"), Blocker] : [F("n1")];

        var plan = Decide(findings, Policy with { Mode = mode });

        Assert.Equal(expected, plan.Decision);
        Assert.Equal(mode == ReviewMode.GateMerge && !hasBlocking, plan.Merge);
    }

    [Fact]
    public void Decide_PostsCommentsInEveryMode()
    {
        var plan = Decide([F("n1"), Blocker], Policy with { Mode = ReviewMode.Comment });

        Assert.Equal(["b1", "n1"], plan.InlineFindings.Select(f => f.Id));
        Assert.Equal(["b1"], plan.BlockingFindingIds);
        Assert.Equal("summary", plan.Summary);
    }

    [Fact]
    public void Decide_DropsLowConfidenceFindings_EvenWhenSevere()
    {
        var plan = Decide([F("low", Severity.Blocker, confidence: 0.59)]);

        Assert.Empty(plan.InlineFindings);
        Assert.Empty(plan.SummaryFindings);
        Assert.Equal(ReviewDecision.Approve, plan.Decision);
    }

    [Theory]
    [InlineData("yarn.lock")]
    [InlineData("web/dist/app.js")]
    public void Decide_DropsFindingsInIgnoredPaths(string file)
    {
        var plan = Decide([F("ignored", Severity.Blocker, file: file)]);

        Assert.Empty(plan.InlineFindings);
        Assert.Empty(plan.SummaryFindings);
        Assert.Equal(ReviewDecision.Approve, plan.Decision);
    }

    [Theory]
    [InlineData("src/Bar.cs", 30)]
    [InlineData("src/Untouched.cs", 10)]
    public void Decide_MovesFindingsOutsideDeltaToSummary_AndNeverBlocksOnThem(string file, int line)
    {
        var outside = F("out", Severity.Blocker, file: file, line: line);

        var plan = Decide([outside]);

        Assert.Empty(plan.InlineFindings);
        Assert.Equal([outside], plan.SummaryFindings);
        Assert.Empty(plan.BlockingFindingIds);
        Assert.Equal(ReviewDecision.Approve, plan.Decision);
    }

    [Fact]
    public void Decide_TreatsFindingOverlappingDeltaRangeAsInside()
    {
        var spanning = F("span", file: "src/Bar.cs") with { StartLine = 5, EndLine = 10 };

        var plan = Decide([spanning]);

        Assert.Equal([spanning], plan.InlineFindings);
    }

    [Fact]
    public void Decide_TreatsFileWithoutRangesAsChangedThroughout()
    {
        var plan = Decide([F("r", file: "src/Renamed.cs", line: 999)]);

        Assert.Single(plan.InlineFindings);
    }

    [Fact]
    public void Decide_OverflowsInlineLimitToSummary_KeepingBlockingFirst()
    {
        Finding[] findings =
        [
            F("nit", Severity.Nit),
            F("minor", Severity.Minor),
            Blocker,
            F("major-style", Severity.Major, Category.Style),
        ];

        var plan = Decide(findings, Policy with { MaxInlineComments = 2 });

        Assert.Equal(["b1", "major-style"], plan.InlineFindings.Select(f => f.Id));
        Assert.Equal(["minor", "nit"], plan.SummaryFindings.Select(f => f.Id));
        Assert.Equal(ReviewDecision.RequestChanges, plan.Decision);
    }

    [Fact]
    public void Decide_BlocksOnFindingsOverInlineLimit()
    {
        var plan = Decide([Blocker], Policy with { MaxInlineComments = 0 });

        Assert.Empty(plan.InlineFindings);
        Assert.Equal(["b1"], plan.BlockingFindingIds);
        Assert.Equal(ReviewDecision.RequestChanges, plan.Decision);
    }

    [Fact]
    public void Decide_KeepsDuplicateFindings()
    {
        var plan = Decide([F("dup"), F("dup")]);

        Assert.Equal(2, plan.InlineFindings.Count);
    }

    [Theory]
    [InlineData(Severity.Major, Category.Correctness, 0.9, true)]
    [InlineData(Severity.Major, Category.Performance, 0.9, false)]
    [InlineData(Severity.Minor, Category.Security, 0.9, false)]
    [InlineData(Severity.Blocker, Category.Style, 0.9, true)]
    public void BlockingRule_MatchesSeverityAndCategory(Severity severity, Category category, double confidence, bool blocking)
    {
        var plan = Decide([F("f", severity, category, confidence)]);

        Assert.Equal(blocking, plan.BlockingFindingIds.Count == 1);
    }

    [Fact]
    public void BlockingRule_RespectsMinConfidence()
    {
        var policy = Policy with { BlockingRules = [new(Severity.Minor, MinConfidence: 0.95)] };

        Assert.Empty(Decide([F("f", confidence: 0.9)], policy).BlockingFindingIds);
        Assert.Single(Decide([F("f", confidence: 0.95)], policy).BlockingFindingIds);
    }

    [Theory]
    [InlineData("reviewer-bot")]
    [InlineData("Reviewer-Bot")]
    public void Decide_NeverApprovesOrBlocksOwnPr(string author)
    {
        var own = Snapshot with { Author = author };

        var blocked = Decide([Blocker], Policy with { Mode = ReviewMode.GateMerge }, own);
        var clean = Decide([F("n1")], Policy with { Mode = ReviewMode.GateMerge }, own);

        Assert.Equal(ReviewDecision.None, blocked.Decision);
        Assert.Equal(ReviewDecision.None, clean.Decision);
        Assert.False(clean.Merge);
        Assert.Single(blocked.InlineFindings);
    }

    [Theory]
    [InlineData(RequestChangesFallback.UnresolvedThreads, true)]
    [InlineData(RequestChangesFallback.Comment, false)]
    public void Decide_FallsBackWhenProviderCannotRequestChanges(RequestChangesFallback fallback, bool unresolvedThreads)
    {
        var plan = Decide([Blocker], Policy with { RequestChangesFallback = fallback }, capabilities: NoRequestChanges);

        Assert.Equal(ReviewDecision.None, plan.Decision);
        Assert.Equal(unresolvedThreads, plan.UnresolvedThreads);
    }

    [Fact]
    public void Decide_DoesNotUseFallbackWhenProviderCanRequestChanges()
    {
        var plan = Decide([Blocker]);

        Assert.False(plan.UnresolvedThreads);
    }

    public static TheoryData<PullRequestSnapshot, string> FailingGuards => new()
    {
        { Snapshot with { Ci = CiState.Pending }, "CI is Pending" },
        { Snapshot with { Ci = CiState.Failure }, "CI is Failure" },
        { Snapshot with { IsMergeable = false }, "not mergeable" },
        { Snapshot with { IsDraft = true }, "draft" },
        { Snapshot with { HeadSha = "def456" }, "head moved" },
    };

    [Theory]
    [MemberData(nameof(FailingGuards))]
    public void Decide_DoesNotMergeWhenAGuardFails(PullRequestSnapshot snapshot, string reason)
    {
        var plan = Decide([F("n1")], Policy with { Mode = ReviewMode.GateMerge }, snapshot);

        Assert.Equal(ReviewDecision.Approve, plan.Decision);
        Assert.False(plan.Merge);
        Assert.Contains(plan.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Decide_MergesWhenAllGuardsPass()
    {
        var plan = Decide([F("n1")], Policy with { Mode = ReviewMode.GateMerge });

        Assert.True(plan.Merge);
        Assert.Contains("Merge guards passed.", plan.Reasons);
    }

    [Fact]
    public void Decide_NeverMergesOutsideGateMerge()
    {
        Assert.False(Decide([F("n1")], Policy with { Mode = ReviewMode.Gate }).Merge);
    }
}
