using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitLab.Tests;

public class GitLabUrlsTests
{
    private static readonly Uri SelfHosted = new("https://git.acme.test/gitlab");

    [Fact]
    public void ApiCloneAndHeadRef_FollowTheHostsPath()
    {
        var repo = new RepoRef(SelfHosted, "acme/platform", "api");

        Assert.Equal(new Uri("https://git.acme.test/gitlab/api/v4/"), GitLabUrls.ApiBase(SelfHosted));
        Assert.Equal(new Uri("https://gitlab.com/api/v4/"), GitLabUrls.ApiBase(GitLabUrls.GitLabCom));
        Assert.Equal("https://git.acme.test/gitlab/acme/platform/api.git", GitLabUrls.CloneUrl(SelfHosted, repo));
        Assert.Equal("refs/merge-requests/7/head", GitLabUrls.MergeRequestHeadRef(7));
    }

    [Theory]
    [InlineData("https://gitlab.com", "https://gitlab.com/acme/api/-/merge_requests/7", "acme", "api", 7)]
    [InlineData("https://gitlab.com", "https://gitlab.com/acme/platform/api/-/merge_requests/12/diffs?view=inline#x", "acme/platform", "api", 12)]
    [InlineData("https://git.acme.test/gitlab", "https://git.acme.test/gitlab/acme/api/-/merge_requests/3", "acme", "api", 3)]
    [InlineData("https://gitlab.com", "https://GitLab.com/Acme/Api/-/merge_requests/9", "Acme", "Api", 9)]
    public void TryParseMergeRequest_AcceptsMergeRequestUrls(string host, string url, string owner, string name, int iid)
    {
        Assert.True(GitLabUrls.TryParseMergeRequest(new Uri(url), new Uri(host), out var pr));
        Assert.Equal((owner, name, iid), (pr.Repo.Owner, pr.Repo.Name, pr.Number));
        Assert.Equal(new Uri(host), pr.Repo.Host);
    }

    [Theory]
    [InlineData("https://gitlab.com", "https://gitlab.com/acme/api/-/issues/7")]
    [InlineData("https://gitlab.com", "https://gitlab.com/acme/api/-/merge_requests/0")]
    [InlineData("https://gitlab.com", "https://gitlab.com/acme/api/-/merge_requests/abc")]
    [InlineData("https://gitlab.com", "https://gitlab.com/api/-/merge_requests/7")]
    [InlineData("https://gitlab.com", "https://github.com/acme/api/-/merge_requests/7")]
    [InlineData("https://gitlab.com", "http://gitlab.com/acme/api/-/merge_requests/7")]
    [InlineData("https://git.acme.test/gitlab", "https://git.acme.test/acme/api/-/merge_requests/7")]
    public void TryParseMergeRequest_RejectsOtherUrls(string host, string url)
    {
        Assert.False(GitLabUrls.TryParseMergeRequest(new Uri(url), new Uri(host), out _));
    }
}

public class DiffLinesTests
{
    private const string Diff = "@@ -8,3 +8,4 @@ class Foo\n line\n line\n+added\n unchanged\n-removed\n\\ No newline at end of file\n@@ -30,1 +31,1 @@\n-old\n+new\n";

    [Fact]
    public void Parse_CountsChangedLines()
    {
        var lines = DiffLines.Parse(Diff);

        Assert.Equal((2, 2), (lines.Additions, lines.Deletions));
    }

    [Theory]
    [InlineData(8, true, 8)]
    [InlineData(10, true, null)]
    [InlineData(11, true, 10)]
    [InlineData(31, true, null)]
    [InlineData(12, false, null)]
    [InlineData(30, false, null)]
    public void TryGetAnchor_KnowsWhichLinesTheDiffShows(int newLine, bool shown, int? oldLine)
    {
        Assert.Equal(shown, DiffLines.Parse(Diff).TryGetAnchor(newLine, out var old));
        Assert.Equal(oldLine, old);
    }

    [Fact]
    public void Anchor_FallsBackToTheFirstLine_WhenTheLastIsOffTheDiff()
    {
        var finding = new Finding("1", Severity.Minor, Category.Style, 0.9, "a.cs", 11, 13, "t", "b", "x");

        Assert.Equal((11, 10, "suggestion:-0+2"), GitLabProvider.Anchor(finding, DiffLines.Parse(Diff)));
        Assert.Null(GitLabProvider.Anchor(finding with { StartLine = 20, EndLine = 21 }, DiffLines.Parse(Diff)));
    }
}
