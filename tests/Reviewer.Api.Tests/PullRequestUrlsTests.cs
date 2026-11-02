using Reviewer.Engine.Config;

namespace Reviewer.Api.Tests;

public class PullRequestUrlsTests
{
    private static readonly ReviewerConfig Both = ConfigLoader.Parse("""
        accounts:
          - { id: gh }
          - { id: work, provider: gitlab, host: https://git.acme.test/gitlab }
        """);

    [Theory]
    [InlineData("https://github.com/acme/api/pull/7", "https://github.com/", "acme", "api", 7)]
    [InlineData("https://git.acme.test/gitlab/acme/platform/api/-/merge_requests/12", "https://git.acme.test/gitlab", "acme/platform", "api", 12)]
    public void TryParse_ReadsEachAccountsUrls(string url, string host, string owner, string name, int number)
    {
        Assert.True(PullRequestUrls.TryParse(new Uri(url), Both, out var pr));
        Assert.Equal((new Uri(host), owner, name, number), (pr.Repo.Host, pr.Repo.Owner, pr.Repo.Name, pr.Number));
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/api/-/merge_requests/7")] // No account on gitlab.com.
    [InlineData("https://git.acme.test/acme/api/-/merge_requests/7")] // Outside the instance's path.
    [InlineData("https://git.acme.test/gitlab/acme/api/pull/7")]
    public void TryParse_RejectsUrlsNoAccountServes(string url)
    {
        Assert.False(PullRequestUrls.TryParse(new Uri(url), Both, out _));
    }

    [Fact]
    public void TryParse_GitHubUrl_ParsesWithoutAGitHubAccount_SoTheErrorCanNameIt()
    {
        var gitlabOnly = ConfigLoader.Parse("accounts: [{ id: gl, provider: gitlab }]");

        Assert.True(PullRequestUrls.TryParse(new Uri("https://github.com/acme/api/pull/7"), gitlabOnly, out var pr));
        Assert.Throws<ConfigException>(() => gitlabOnly.For(pr.Repo));
    }
}
