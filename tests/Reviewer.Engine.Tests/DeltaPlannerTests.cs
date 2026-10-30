using Reviewer.Git;
using Reviewer.Git.Tests;

namespace Reviewer.Engine.Tests;

public sealed class DeltaPlannerTests : IDisposable
{
    private readonly SourceRepo _source = new();

    public void Dispose() => _source.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    // main: a.txt and b.txt. feature: one commit changing each.
    private string SetUp()
    {
        _source.Write("a.txt", "a1", "a2", "a3");
        _source.Write("b.txt", "b1", "b2", "b3");
        _source.Commit("base");
        _source.Git("switch", "--quiet", "-c", "feature");
        _source.Write("a.txt", "a1", "A2", "a3");
        _source.Commit("change a");
        _source.Write("b.txt", "b1", "B2", "b3");
        return _source.Commit("change b");
    }

    private async Task<(ReviewDelta Delta, ReviewedState Current)> PlanAsync(ReviewedState? previous)
    {
        var git = await GitRepository.OpenOrInitAsync(_source.NewDirectory("cache.git"), Ct);
        await git.FetchAsync(_source.Url, ["+feature:refs/reviewer/head", "+main:refs/reviewer/base"], null, Ct);
        var head = _source.Git("rev-parse", "feature").Trim();
        var mergeBase = await git.MergeBaseAsync(_source.Git("rev-parse", "main").Trim(), head, Ct);
        return await DeltaPlanner.PlanAsync(git, mergeBase, head, previous, Ct);
    }

    private void OnMain(Action change)
    {
        _source.Git("switch", "--quiet", "main");
        change();
        _source.Git("switch", "--quiet", "feature");
    }

    [Fact]
    public async Task FirstReview_IsFull_AndRecordsState()
    {
        var head = SetUp();

        var (delta, current) = await PlanAsync(null);

        Assert.Equal(DeltaKind.Full, delta.Kind);
        Assert.Null(delta.Paths);
        Assert.Equal(head, current.HeadSha);
        Assert.Equal(2, current.CommitPatchIds.Count);
        Assert.Equal(["a.txt", "b.txt"], current.FilePatchIds.Keys.Order());
    }

    [Fact]
    public async Task SameHead_IsUnchanged()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.Unchanged, delta.Kind);
    }

    [Fact]
    public async Task Push_ReviewsOnlyTheNewCommits()
    {
        var reviewedHead = SetUp();
        var (_, reviewed) = await PlanAsync(null);
        _source.Write("b.txt", "b1", "B2", "B3");
        _source.Commit("more b");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.NewCommits, delta.Kind);
        Assert.Equal(reviewedHead, delta.FromSha);
        Assert.Equal(["b.txt"], delta.Paths!);
    }

    [Fact]
    public async Task MergingTheBaseIn_ChangesNoPrFile_IsUnchanged()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);
        OnMain(() =>
        {
            _source.Write("c.txt", "c");
            _source.Commit("main moves on");
        });
        _source.Git("merge", "--quiet", "--no-edit", "main");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.Unchanged, delta.Kind);
    }

    [Fact]
    public async Task Rebase_SkipsMatchedCommits_AndReviewsTheNewOnes()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);
        OnMain(() =>
        {
            _source.Write("c.txt", "c");
            _source.Commit("main moves on");
        });
        _source.Git("rebase", "--quiet", "main");
        var rebasedOldHead = _source.Head();
        _source.Write("a.txt", "a1", "A2", "A3");
        _source.Commit("new on top");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.Rebased, delta.Kind);
        Assert.Equal(rebasedOldHead, delta.FromSha);
        Assert.Equal(["a.txt", "b.txt"], delta.Paths!.Order());
    }

    [Fact]
    public async Task Rebase_WithoutNewCommits_IsUnchanged()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);
        OnMain(() =>
        {
            _source.Write("c.txt", "c");
            _source.Commit("main moves on");
        });
        _source.Git("rebase", "--quiet", "main");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.Unchanged, delta.Kind);
    }

    [Fact]
    public async Task Squash_WithOneFileEdited_ReviewsOnlyThatFile()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);
        _source.Git("reset", "--quiet", "--soft", "main");
        _source.Write("b.txt", "b1", "B2!", "b3");
        _source.Commit("squashed, b edited");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.ChangedFiles, delta.Kind);
        Assert.Equal(["b.txt"], delta.Paths!);
    }

    [Fact]
    public async Task Squash_WithEveryFileUnchanged_IsUnchanged()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);
        _source.Git("reset", "--quiet", "--soft", "main");
        _source.Commit("squashed");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.Unchanged, delta.Kind);
    }

    [Fact]
    public async Task Rewrite_ChangingEveryFile_IsFull()
    {
        SetUp();
        var (_, reviewed) = await PlanAsync(null);
        _source.Git("reset", "--quiet", "--hard", "main");
        _source.Write("a.txt", "a1", "X", "a3");
        _source.Write("b.txt", "b1", "Y", "b3");
        _source.Commit("start over");

        var (delta, _) = await PlanAsync(reviewed);

        Assert.Equal(DeltaKind.Full, delta.Kind);
    }
}
