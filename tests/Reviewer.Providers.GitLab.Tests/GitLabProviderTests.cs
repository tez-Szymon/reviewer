using System.Net;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitLab.Tests;

public class GitLabProviderTests
{
    private const string Sha = "head123";

    // A project in a subgroup: the full path is one escaped segment.
    private const string Project = "projects/acme%2Fplatform%2Fapi";
    private const string Mr = Project + "/merge_requests/7";

    private static readonly PullRequestRef Pr = new(new RepoRef(FakeGitLab.Host, "acme/platform", "api"), 7);
    private static CancellationToken Ct => CancellationToken.None;

    private const string Me = """{ "id": 42, "username": "reviewer-bot" }""";

    private const string MrJson = """
        {
          "iid": 7,
          "title": "Fix totals",
          "description": "Rounds half up.",
          "sha": "head123",
          "target_branch": "main",
          "author": { "id": 1, "username": "alice" },
          "assignees": [{ "id": 2, "username": "bob" }],
          "reviewers": [{ "id": 42, "username": "reviewer-bot" }],
          "labels": ["backend"],
          "draft": false,
          "diff_refs": { "base_sha": "mergebase", "head_sha": "head123", "start_sha": "main000" },
          "head_pipeline": { "status": "success" },
          "detailed_merge_status": "mergeable"
        }
        """;

    // Line 10 was added; line 11 is unchanged (old line 10); line 20 is outside the diff.
    private const string Diff = "@@ -8,3 +8,4 @@ class Foo\\n line\\n line\\n+added\\n unchanged\\n-removed\\n";

    private const string Versions = """[{ "id": 3, "head_commit_sha": "head123", "base_commit_sha": "mergebase", "start_commit_sha": "main000" }]""";

    private static readonly string Version = $$"""
        {
          "id": 3, "head_commit_sha": "head123", "base_commit_sha": "mergebase", "start_commit_sha": "main000",
          "diffs": [{ "old_path": "src/Old.cs", "new_path": "src/Foo.cs", "diff": "{{Diff}}", "deleted_file": false }]
        }
        """;

    private static InlineComment Comment(string id, int start, int end, string? suggestion = null) =>
        new(new Finding(id, Severity.Major, Category.Correctness, 0.9, "src/Foo.cs", start, end, $"Title {id}", $"Body {id}", suggestion), $"fp-{id}");

    private static ReviewResult Result(ReviewDecision decision, params InlineComment[] comments) =>
        new("Looks fine.", comments, [], decision, Sha);

    private static FakeGitLab ForPublish(string approvedBy = "[]") => new FakeGitLab()
        .On($"GET {Mr}/versions", Versions)
        .On($"GET {Mr}/versions/3", Version)
        .On($"POST {Mr}/discussions", "{}", HttpStatusCode.Created)
        .On($"POST {Mr}/notes", "{}", HttpStatusCode.Created)
        .On("GET user", Me)
        .On($"GET {Mr}/approvals", $$"""{ "approved_by": {{approvedBy}} }""")
        .On($"POST {Mr}/approve", "{}", HttpStatusCode.Created)
        .On($"POST {Mr}/unapprove", "{}", HttpStatusCode.Created);

    [Fact]
    public async Task GetSnapshot_MapsMergeRequest()
    {
        var gitlab = new FakeGitLab().On($"GET {Mr}", MrJson);

        var snapshot = await gitlab.Provider().GetSnapshotAsync(Pr, Ct);

        Assert.Equal("Fix totals", snapshot.Title);
        Assert.Equal("Rounds half up.", snapshot.Description);
        Assert.Equal(Sha, snapshot.HeadSha);
        Assert.Equal("main000", snapshot.BaseSha); // The target branch's head, not the merge base.
        Assert.Equal("main", snapshot.BaseBranch);
        Assert.Equal("alice", snapshot.Author);
        Assert.Equal(["bob"], snapshot.Assignees);
        Assert.Equal(["reviewer-bot"], snapshot.RequestedReviewers);
        Assert.Equal(["backend"], snapshot.Labels);
        Assert.False(snapshot.IsDraft);
        Assert.True(snapshot.IsMergeable);
        Assert.Equal(CiState.Success, snapshot.Ci);
    }

    [Theory]
    [InlineData("""null""", CiState.Unknown)]
    [InlineData("""{ "status": "skipped" }""", CiState.Unknown)]
    [InlineData("""{ "status": "running" }""", CiState.Pending)]
    [InlineData("""{ "status": "manual" }""", CiState.Pending)]
    [InlineData("""{ "status": "failed" }""", CiState.Failure)]
    [InlineData("""{ "status": "canceled" }""", CiState.Failure)]
    public async Task GetSnapshot_MapsHeadPipeline(string pipeline, CiState expected)
    {
        var json = MrJson.Replace("""{ "status": "success" }""", pipeline, StringComparison.Ordinal);

        Assert.Equal(expected, (await new FakeGitLab().On($"GET {Mr}", json).Provider().GetSnapshotAsync(Pr, Ct)).Ci);
    }

    [Theory]
    [InlineData("\"detailed_merge_status\": \"checking\"", false)]
    [InlineData("\"detailed_merge_status\": \"not_approved\"", false)]
    [InlineData("\"merge_status\": \"can_be_merged\"", true)]
    [InlineData("\"merge_status\": \"cannot_be_merged\"", false)]
    public async Task GetSnapshot_IsMergeableOnlyWhenGitLabSaysSo(string status, bool expected)
    {
        var json = MrJson.Replace("\"detailed_merge_status\": \"mergeable\"", status, StringComparison.Ordinal);

        Assert.Equal(expected, (await new FakeGitLab().On($"GET {Mr}", json).Provider().GetSnapshotAsync(Pr, Ct)).IsMergeable);
    }

    [Fact]
    public async Task GetSnapshot_WithoutDiffRefsYet_Throws()
    {
        var json = MrJson.Replace("""{ "base_sha": "mergebase", "head_sha": "head123", "start_sha": "main000" }""", "null", StringComparison.Ordinal);

        var e = await Assert.ThrowsAsync<GitLabApiException>(() => new FakeGitLab().On($"GET {Mr}", json).Provider().GetSnapshotAsync(Pr, Ct));
        Assert.Contains("still preparing", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListOpen_MapsEveryPage_AndPassesTheETagBack()
    {
        var list = $"{Project}/merge_requests?state=opened&order_by=updated_at&sort=desc&per_page=100";
        var gitlab = new FakeGitLab()
            .On($"GET {list}", $"[{MrJson}]", nextPage: $"{list}&page=2", etag: "W/\"v1\"")
            .On($"GET {list}&page=2", $"[{MrJson.Replace("\"iid\": 7", "\"iid\": 8", StringComparison.Ordinal)}]")
            .On($"GET {list}", "", HttpStatusCode.NotModified);
        var provider = gitlab.Provider();

        var first = await provider.ListOpenPullRequestsAsync(Pr.Repo, null, Ct);
        var second = await provider.ListOpenPullRequestsAsync(Pr.Repo, first.ETag, Ct);

        Assert.Equal([7, 8], first.PullRequests.Select(p => p.Ref.Number));
        var mr = first.PullRequests[0];
        Assert.Equal((Sha, "main", "alice"), (mr.HeadSha, mr.BaseBranch, mr.Author));
        Assert.Equal(["reviewer-bot"], mr.RequestedReviewers);
        Assert.Equal("W/\"v1\"", first.ETag);
        Assert.True(second.NotModified);
        Assert.Equal([null, null, "W/\"v1\""], gitlab.IfNoneMatch);
    }

    [Fact]
    public async Task GetChangedFiles_CountsLinesFromTheDiffs()
    {
        var gitlab = new FakeGitLab().On($"GET {Mr}/diffs?per_page=100", $$"""
            [
              { "old_path": "src/Old.cs", "new_path": "src/Foo.cs", "diff": "{{Diff}}", "deleted_file": false },
              { "old_path": "gone.txt", "new_path": "gone.txt", "diff": "@@ -1,2 +0,0 @@\n-a\n-b\n", "deleted_file": true },
              { "old_path": "big.json", "new_path": "big.json", "diff": "", "deleted_file": false }
            ]
            """);

        var files = await gitlab.Provider().GetChangedFilesAsync(Pr, Ct);

        Assert.Equal(
            [new ChangedFile("src/Foo.cs", 1, 1), new ChangedFile("gone.txt", 0, 2), new ChangedFile("big.json", 0, 0)],
            files);
    }

    [Fact]
    public async Task GetOwnComments_KeepsOurMarkedDiscussions_WithTheirResolvedState()
    {
        var gitlab = new FakeGitLab()
            .On("GET user", Me)
            .On($"GET {Mr}/discussions?per_page=100", """
                [
                  { "id": "d1", "notes": [{ "id": 1, "body": "x\n<!-- reviewer:fp=fp-1 -->", "author": { "id": 42, "username": "reviewer-bot" },
                    "position": { "new_path": "src/Foo.cs", "new_line": 10 }, "system": false, "resolvable": true, "resolved": true }] },
                  { "id": "d2", "notes": [{ "id": 2, "body": "y\n<!-- reviewer:fp=fp-2 -->", "author": { "id": 1, "username": "alice" },
                    "system": false, "resolvable": true, "resolved": false }] },
                  { "id": "d3", "notes": [{ "id": 3, "body": "summary", "author": { "id": 42, "username": "reviewer-bot" },
                    "system": false, "resolvable": false, "resolved": false }] },
                  { "id": "d4", "notes": [{ "id": 4, "body": "z\n<!-- reviewer:fp=fp-4 -->", "author": { "id": 42, "username": "Reviewer-Bot" },
                    "position": { "old_path": "a.cs", "old_line": 3 }, "system": false, "resolvable": true, "resolved": false }] }
                ]
                """);

        var comments = await gitlab.Provider().GetOwnCommentsAsync(Pr, Ct);

        Assert.Equal(
            [new PublishedComment("d1", "fp-1", "src/Foo.cs", 10, IsResolved: true), new PublishedComment("d4", "fp-4", "a.cs", 3)],
            comments);
    }

    [Fact]
    public async Task Publish_PostsDiscussionsOnTheReviewedVersion_ThenTheSummary_ThenApproves()
    {
        var gitlab = ForPublish();

        await gitlab.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.Approve, Comment("a", 10, 10), Comment("b", 9, 11, "fixed();")), Ct);

        var discussions = gitlab.Requests.Where(r => r.Route == $"POST {Mr}/discussions").Select(r => r.Body!.Value).ToList();
        Assert.Equal(2, discussions.Count);

        var added = discussions[0].GetProperty("position");
        Assert.Equal("text", added.GetProperty("position_type").GetString());
        Assert.Equal(("mergebase", "main000", "head123"), (
            added.GetProperty("base_sha").GetString(), added.GetProperty("start_sha").GetString(), added.GetProperty("head_sha").GetString()));
        Assert.Equal(("src/Old.cs", "src/Foo.cs", 10), (
            added.GetProperty("old_path").GetString(), added.GetProperty("new_path").GetString(), added.GetProperty("new_line").GetInt32()));
        Assert.False(added.TryGetProperty("old_line", out _)); // An added line has no old line.

        // An unchanged line needs its old line too; the suggestion replaces the two lines above it as well.
        var unchanged = discussions[1];
        Assert.Equal((11, 10), (unchanged.GetProperty("position").GetProperty("new_line").GetInt32(), unchanged.GetProperty("position").GetProperty("old_line").GetInt32()));
        Assert.Contains("```suggestion:-2+0\nfixed();\n```", unchanged.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("<!-- reviewer:fp=fp-b -->", unchanged.GetProperty("body").GetString(), StringComparison.Ordinal);

        Assert.Equal("Looks fine.", gitlab.Body($"POST {Mr}/notes").GetProperty("body").GetString());
        Assert.Equal(Sha, gitlab.Body($"POST {Mr}/approve").GetProperty("sha").GetString());
        Assert.Equal(
            [$"POST {Mr}/notes", "GET user", $"GET {Mr}/approvals", $"POST {Mr}/approve"],
            gitlab.Routes.SkipWhile(r => r != $"POST {Mr}/notes"));
    }

    [Fact]
    public async Task Publish_FindingOffTheDiff_GoesToTheSummary()
    {
        var gitlab = ForPublish();

        await gitlab.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.None, Comment("far", 20, 20)), Ct);

        Assert.DoesNotContain($"POST {Mr}/discussions", gitlab.Routes);
        Assert.Contains("`src/Foo.cs:20`: Title far", gitlab.Body($"POST {Mr}/notes").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_FindingGitLabTurnsDown_GoesToTheSummary()
    {
        var gitlab = new FakeGitLab()
            .On($"GET {Mr}/versions", Versions)
            .On($"GET {Mr}/versions/3", Version)
            .On($"POST {Mr}/discussions", """{ "message": "line_code can't be blank" }""", HttpStatusCode.BadRequest)
            .On($"POST {Mr}/notes", "{}", HttpStatusCode.Created)
            .On("GET user", Me)
            .On($"GET {Mr}/approvals", """{ "approved_by": [] }""");

        await gitlab.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.None, Comment("a", 10, 10)), Ct);

        Assert.Contains("`src/Foo.cs:10`: Title a", gitlab.Body($"POST {Mr}/notes").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_HeadNoLongerAVersion_ListsEveryFindingInTheSummary()
    {
        var moved = new FakeGitLab()
            .On($"GET {Mr}/versions", """[{ "id": 4, "head_commit_sha": "newer", "base_commit_sha": "b", "start_commit_sha": "s" }]""")
            .On($"POST {Mr}/notes", "{}", HttpStatusCode.Created)
            .On("GET user", Me)
            .On($"GET {Mr}/approvals", """{ "approved_by": [] }""");

        await moved.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.None, Comment("a", 10, 10)), Ct);

        Assert.DoesNotContain($"POST {Mr}/discussions", moved.Routes);
        Assert.Contains("Title a", moved.Body($"POST {Mr}/notes").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AlreadyApproved_DoesNotApproveAgain()
    {
        var gitlab = ForPublish(approvedBy: """[{ "user": { "id": 42, "username": "reviewer-bot" } }]""");

        await gitlab.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.Approve), Ct);

        Assert.DoesNotContain($"POST {Mr}/approve", gitlab.Routes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Publish_WithdrawsOurApproval_OnlyWhenThePolicyAsks(bool withdraw, bool unapproved)
    {
        var gitlab = ForPublish(approvedBy: """[{ "user": { "id": 42, "username": "reviewer-bot" } }]""");

        await gitlab.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.None) with { WithdrawApproval = withdraw }, Ct);

        Assert.Equal(unapproved, gitlab.Routes.Contains($"POST {Mr}/unapprove"));
    }

    [Fact]
    public async Task Publish_WithdrawWithoutOurApproval_DoesNothing()
    {
        var gitlab = ForPublish(approvedBy: """[{ "user": { "id": 1, "username": "alice" } }]""");

        await gitlab.Provider().PublishReviewAsync(Pr, Result(ReviewDecision.None) with { WithdrawApproval = true }, Ct);

        Assert.DoesNotContain($"POST {Mr}/unapprove", gitlab.Routes);
    }

    [Fact]
    public async Task Resolve_RepliesThenResolvesTheDiscussion()
    {
        var gitlab = new FakeGitLab()
            .On($"GET {Mr}/discussions/d1", """{ "id": "d1", "notes": [{ "id": 1, "body": "x", "system": false, "resolvable": true, "resolved": false }] }""")
            .On($"POST {Mr}/discussions/d1/notes", "{}", HttpStatusCode.Created)
            .On($"PUT {Mr}/discussions/d1", "{}");

        await gitlab.Provider().ResolveCommentAsync(Pr, new PublishedComment("d1", "fp-1", "src/Foo.cs", 10), "Fixed in abc.", Ct);

        Assert.Equal("Fixed in abc.", gitlab.Body($"POST {Mr}/discussions/d1/notes").GetProperty("body").GetString());
        Assert.True(gitlab.Body($"PUT {Mr}/discussions/d1").GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task Resolve_AlreadyResolved_LeavesItAlone()
    {
        var gitlab = new FakeGitLab()
            .On($"GET {Mr}/discussions/d1", """{ "id": "d1", "notes": [{ "id": 1, "body": "x", "system": false, "resolvable": true, "resolved": true }] }""");

        await gitlab.Provider().ResolveCommentAsync(Pr, new PublishedComment("d1", "fp-1", "src/Foo.cs", 10), "Fixed.", Ct);

        Assert.Equal([$"GET {Mr}/discussions/d1"], gitlab.Routes);
    }

    [Theory]
    [InlineData(MergeMethod.Merge, false)]
    [InlineData(MergeMethod.Squash, true)]
    public async Task Merge_SendsTheReviewedSha(MergeMethod method, bool squash)
    {
        var gitlab = new FakeGitLab().On($"PUT {Mr}/merge", "{}");

        await gitlab.Provider().MergeAsync(Pr, new MergeOptions(method, Sha), Ct);

        var body = gitlab.Body($"PUT {Mr}/merge");
        Assert.Equal((Sha, squash), (body.GetProperty("sha").GetString(), body.GetProperty("squash").GetBoolean()));
    }

    [Theory]
    [InlineData("""{ "message": "401 Unauthorized" }""", "401 Unauthorized")]
    [InlineData("""{ "message": { "base": ["Branch cannot be merged"] } }""", """{ "base": ["Branch cannot be merged"] }""")]
    [InlineData("""{ "error": "insufficient_scope" }""", "insufficient_scope")]
    [InlineData("<html>gateway</html>", "")]
    public async Task Errors_CarryGitLabsMessage(string body, string message)
    {
        var gitlab = new FakeGitLab().On("GET user", body, HttpStatusCode.Forbidden);

        var e = await Assert.ThrowsAsync<GitLabApiException>(() => gitlab.Provider().GetCurrentUserAsync(Ct));

        Assert.Equal(HttpStatusCode.Forbidden, e.Status);
        Assert.Equal($"GitLab API returned 403 Forbidden: {message}", e.Message);
    }
}
