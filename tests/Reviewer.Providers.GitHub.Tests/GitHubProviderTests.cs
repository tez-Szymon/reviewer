using System.Net;
using System.Text.Json;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub.Tests;

public class GitHubProviderTests
{
    private const string Sha = "abc123";
    private const string Pull = "repos/acme/api/pulls/7";

    private static readonly PullRequestRef Pr = new(new RepoRef(GitHubUrls.GitHubCom, "acme", "api"), 7);
    private static CancellationToken Ct => CancellationToken.None;

    private const string PullJson = """
        {
          "head": { "sha": "abc123", "ref": "feature" },
          "base": { "sha": "base000", "ref": "main" },
          "user": { "login": "alice" },
          "assignees": [{ "login": "bob" }],
          "requested_reviewers": [{ "login": "reviewer-bot" }],
          "labels": [{ "name": "backend" }],
          "draft": false,
          "mergeable": true
        }
        """;

    private static FakeGitHub WithPull(string status, string checkRuns, string pull = PullJson) => new FakeGitHub()
        .On($"GET {Pull}", pull)
        .On($"GET repos/acme/api/commits/{Sha}/status", status)
        .On($"GET repos/acme/api/commits/{Sha}/check-runs?per_page=100", checkRuns);

    private const string NoStatuses = """{ "state": "pending", "total_count": 0 }""";
    private const string NoChecks = """{ "total_count": 0, "check_runs": [] }""";

    private static string Checks(params string[] runs) =>
        $$"""{ "total_count": {{runs.Length}}, "check_runs": [{{string.Join(",", runs)}}] }""";

    private static string Run(string status, string? conclusion = null) =>
        conclusion is null ? $$"""{ "status": "{{status}}" }""" : $$"""{ "status": "{{status}}", "conclusion": "{{conclusion}}" }""";

    private static InlineComment Comment(string id, int start, int end, string? suggestion = null) =>
        new(new Finding(id, Severity.Major, Category.Correctness, 0.9, "src/Foo.cs", start, end, $"Title {id}", $"Body {id}", suggestion), $"fp-{id}");

    [Fact]
    public async Task GetSnapshot_MapsPullRequest()
    {
        var github = WithPull(NoStatuses, Checks(Run("completed", "success")));

        var snapshot = await github.Provider().GetSnapshotAsync(Pr, Ct);

        Assert.Equal(Sha, snapshot.HeadSha);
        Assert.Equal("base000", snapshot.BaseSha);
        Assert.Equal("main", snapshot.BaseBranch);
        Assert.Equal("alice", snapshot.Author);
        Assert.Equal(["bob"], snapshot.Assignees);
        Assert.Equal(["reviewer-bot"], snapshot.RequestedReviewers);
        Assert.Equal(["backend"], snapshot.Labels);
        Assert.False(snapshot.IsDraft);
        Assert.True(snapshot.IsMergeable);
        Assert.Equal(CiState.Success, snapshot.Ci);
    }

    [Fact]
    public async Task GetSnapshot_TreatsUnknownMergeabilityAsNotMergeable()
    {
        var github = WithPull(NoStatuses, NoChecks, PullJson.Replace("\"mergeable\": true", "\"mergeable\": null", StringComparison.Ordinal));

        Assert.False((await github.Provider().GetSnapshotAsync(Pr, Ct)).IsMergeable);
    }

    public static TheoryData<string, string, CiState> CiCases => new()
    {
        { NoStatuses, NoChecks, CiState.Unknown },
        { """{ "state": "success", "total_count": 2 }""", NoChecks, CiState.Success },
        { """{ "state": "pending", "total_count": 1 }""", Checks(Run("completed", "success")), CiState.Pending },
        { """{ "state": "error", "total_count": 1 }""", Checks(Run("completed", "success")), CiState.Failure },
        { NoStatuses, Checks(Run("completed", "success"), Run("completed", "skipped"), Run("completed", "neutral")), CiState.Success },
        { NoStatuses, Checks(Run("completed", "success"), Run("in_progress")), CiState.Pending },
        { NoStatuses, Checks(Run("queued"), Run("completed", "timed_out")), CiState.Failure },
        { NoStatuses, Checks(Run("completed", "cancelled")), CiState.Failure },
    };

    [Theory]
    [MemberData(nameof(CiCases))]
    public async Task GetSnapshot_CombinesStatusesAndCheckRuns(string status, string checkRuns, CiState expected)
    {
        var github = WithPull(status, checkRuns);

        Assert.Equal(expected, (await github.Provider().GetSnapshotAsync(Pr, Ct)).Ci);
    }

    [Fact]
    public async Task GetSnapshot_ReadsAllCheckRunPages()
    {
        var github = new FakeGitHub()
            .On($"GET {Pull}", PullJson)
            .On($"GET repos/acme/api/commits/{Sha}/status", NoStatuses)
            .On($"GET repos/acme/api/commits/{Sha}/check-runs?per_page=100", Checks(Run("completed", "success")), nextPage: $"repos/acme/api/commits/{Sha}/check-runs?per_page=100&page=2")
            .On($"GET repos/acme/api/commits/{Sha}/check-runs?per_page=100&page=2", Checks(Run("completed", "failure")));

        Assert.Equal(CiState.Failure, (await github.Provider().GetSnapshotAsync(Pr, Ct)).Ci);
    }

    [Fact]
    public async Task GetCurrentUser_IsCached()
    {
        var github = new FakeGitHub().On("GET user", """{ "login": "reviewer-bot" }""");
        var provider = github.Provider();

        Assert.Equal("reviewer-bot", await provider.GetCurrentUserAsync(Ct));
        Assert.Equal("reviewer-bot", await provider.GetCurrentUserAsync(Ct));
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task GetOwnComments_KeepsOnlyOurMarkedComments_AcrossPages()
    {
        var github = new FakeGitHub()
            .On("GET user", """{ "login": "reviewer-bot" }""")
            .On($"GET {Pull}/comments?per_page=100", """
                [
                  { "id": 1, "user": { "login": "Reviewer-Bot" }, "body": "x\n<!-- reviewer:fp=fp-1 -->", "path": "a.cs", "line": 3 },
                  { "id": 2, "user": { "login": "reviewer-bot" }, "body": "manual note, no marker", "path": "a.cs", "line": 4 },
                  { "id": 3, "user": { "login": "mallory" }, "body": "<!-- reviewer:fp=fp-forged -->", "path": "a.cs", "line": 5 }
                ]
                """, nextPage: $"{Pull}/comments?per_page=100&page=2")
            .On($"GET {Pull}/comments?per_page=100&page=2", """
                [{ "id": 4, "user": { "login": "reviewer-bot" }, "body": "y\n<!-- reviewer:fp=fp-4 -->", "path": "b.cs", "line": null }]
                """);

        var comments = await github.Provider().GetOwnCommentsAsync(Pr, Ct);

        Assert.Equal(
            [new PublishedComment("1", "fp-1", "a.cs", 3), new PublishedComment("4", "fp-4", "b.cs", null)],
            comments);
    }

    [Theory]
    [InlineData(ReviewDecision.None, "COMMENT")]
    [InlineData(ReviewDecision.Approve, "APPROVE")]
    [InlineData(ReviewDecision.RequestChanges, "REQUEST_CHANGES")]
    public async Task PublishReview_PostsOneReviewBoundToReviewedCommit(ReviewDecision decision, string reviewEvent)
    {
        var github = new FakeGitHub().On($"POST {Pull}/reviews", "{}");
        var result = new ReviewResult("All good.", [Comment("a", 10, 10), Comment("b", 3, 6, "fixed()")], [], decision, Sha);

        await github.Provider().PublishReviewAsync(Pr, result, Ct);

        var body = Assert.Single(github.Requests).Body!.Value;
        Assert.Equal(Sha, body.GetProperty("commit_id").GetString());
        Assert.Equal(reviewEvent, body.GetProperty("event").GetString());
        Assert.Equal("All good.", body.GetProperty("body").GetString());

        var comments = body.GetProperty("comments").EnumerateArray().ToList();
        Assert.Equal(2, comments.Count);
        Assert.Equal("src/Foo.cs", comments[0].GetProperty("path").GetString());
        Assert.Equal(10, comments[0].GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", comments[0].GetProperty("side").GetString());
        Assert.False(comments[0].TryGetProperty("start_line", out _));
        Assert.Equal(3, comments[1].GetProperty("start_line").GetInt32());
        Assert.Equal(6, comments[1].GetProperty("line").GetInt32());
        Assert.EndsWith("<!-- reviewer:fp=fp-b -->", comments[1].GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains("```suggestion\nfixed()\n```", comments[1].GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReview_MovesCommentsIntoBody_WhenGitHubRejectsAnchors()
    {
        var github = new FakeGitHub()
            .On($"POST {Pull}/reviews", """{ "message": "Unprocessable Entity" }""", HttpStatusCode.UnprocessableEntity)
            .On($"POST {Pull}/reviews", "{}");
        var result = new ReviewResult("Summary.", [Comment("a", 10, 10)], [Comment("c", 99, 99).Finding], ReviewDecision.RequestChanges, Sha);

        await github.Provider().PublishReviewAsync(Pr, result, Ct);

        Assert.Equal(2, github.Requests.Count);
        var retry = github.Requests[1].Body!.Value;
        Assert.Equal("REQUEST_CHANGES", retry.GetProperty("event").GetString());
        Assert.Empty(retry.GetProperty("comments").EnumerateArray());
        Assert.Contains("`src/Foo.cs:10`: Title a", retry.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains("`src/Foo.cs:99`: Title c", retry.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReview_ListsSummaryFindingsInBody()
    {
        var github = new FakeGitHub().On($"POST {Pull}/reviews", "{}");
        var result = new ReviewResult("Summary.", [Comment("a", 10, 10)], [Comment("c", 99, 99).Finding], ReviewDecision.None, Sha);

        await github.Provider().PublishReviewAsync(Pr, result, Ct);

        var body = github.Requests[0].Body!.Value.GetProperty("body").GetString();
        Assert.Contains("### Other findings", body, StringComparison.Ordinal);
        Assert.Contains("`src/Foo.cs:99`: Title c", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Title a", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReview_SurfacesGitHubErrors()
    {
        var github = new FakeGitHub()
            .On($"POST {Pull}/reviews", """{ "message": "Bad credentials" }""", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            github.Provider().PublishReviewAsync(Pr, new ReviewResult("s", [], [], ReviewDecision.None, Sha), Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        Assert.Contains("Bad credentials", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_SendsMethodAndExpectedHead()
    {
        var github = new FakeGitHub().On($"PUT {Pull}/merge", """{ "merged": true }""");

        await github.Provider().MergeAsync(Pr, new MergeOptions(MergeMethod.Squash, Sha), Ct);

        var body = Assert.Single(github.Requests).Body!.Value;
        Assert.Equal("squash", body.GetProperty("merge_method").GetString());
        Assert.Equal(Sha, body.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Merge_FailsWhenHeadMoved()
    {
        var github = new FakeGitHub().On($"PUT {Pull}/merge", """{ "message": "Head branch was modified." }""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            github.Provider().MergeAsync(Pr, new MergeOptions(MergeMethod.Merge, Sha), Ct));

        Assert.Equal(HttpStatusCode.Conflict, error.Status);
    }
}
