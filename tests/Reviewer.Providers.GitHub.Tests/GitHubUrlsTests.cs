using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub.Tests;

public class GitHubUrlsTests
{
    [Fact]
    public void CloneUrlAndPullHeadRef_PointAtThePullRequest()
    {
        var repo = new RepoRef(GitHubUrls.GitHubCom, "acme", "api");

        Assert.Equal("https://github.com/acme/api.git", GitHubUrls.CloneUrl(repo));
        Assert.Equal("refs/pull/7/head", GitHubUrls.PullHeadRef(7));
    }

    [Theory]
    [InlineData("https://github.com/acme/api/pull/7", "acme", "api", 7)]
    [InlineData("https://github.com/acme/api/pull/7/files?diff=split#x", "acme", "api", 7)]
    [InlineData("https://GitHub.com/Acme/Api/pull/12", "Acme", "Api", 12)]
    public void TryParsePullRequest_AcceptsPullUrls(string url, string owner, string name, int number)
    {
        Assert.True(GitHubUrls.TryParsePullRequest(new Uri(url), out var pr));
        Assert.Equal((owner, name, number), (pr.Repo.Owner, pr.Repo.Name, pr.Number));
    }

    [Theory]
    [InlineData("https://github.com/acme/api/issues/7")]
    [InlineData("https://github.com/acme/api/pull/0")]
    [InlineData("https://github.com/acme/api/pull/abc")]
    [InlineData("https://gitlab.com/acme/api/pull/7")]
    [InlineData("https://github.com/acme/api")]
    public void TryParsePullRequest_RejectsOtherUrls(string url)
    {
        Assert.False(GitHubUrls.TryParsePullRequest(new Uri(url), out _));
    }
}
