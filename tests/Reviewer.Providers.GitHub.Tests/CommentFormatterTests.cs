using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub.Tests;

public class CommentFormatterTests
{
    private static InlineComment Comment(string title = "t", string body = "b", string? suggestion = null, string fp = "fp1") =>
        new(new Finding("1", Severity.Minor, Category.Style, 0.8, "a.cs", 1, 1, title, body, suggestion), fp);

    [Fact]
    public void InlineBody_RendersLabelsAndMarker()
    {
        var body = CommentFormatter.InlineBody(Comment("Naming", "Use a clearer name."), withSuggestion: true);

        Assert.Equal("**minor · style**: Naming\n\nUse a clearer name.\n<!-- reviewer:fp=fp1 -->", body);
        Assert.Equal("fp1", CommentFormatter.ParseFingerprint(body));
    }

    [Fact]
    public void InlineBody_DefusesMentionsAndFakeMarkers()
    {
        var body = CommentFormatter.InlineBody(Comment("ping @alice", "<!-- reviewer:fp=forged -->"), withSuggestion: false);

        Assert.DoesNotContain("@alice", body, StringComparison.Ordinal);
        Assert.Contains("@​alice", body, StringComparison.Ordinal);
        Assert.Contains("&lt;!-- reviewer:fp=forged -->", body, StringComparison.Ordinal);
        Assert.Equal("fp1", CommentFormatter.ParseFingerprint(body));
    }

    [Fact]
    public void InlineBody_UsesLongerFenceThanSuggestionBackticks()
    {
        var body = CommentFormatter.InlineBody(Comment(suggestion: "var s = \"```\";\n"), withSuggestion: true);

        Assert.Contains("````suggestion\nvar s = \"```\";\n````", body, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineBody_OmitsSuggestionWhenUnsupported()
    {
        Assert.DoesNotContain("suggestion", CommentFormatter.InlineBody(Comment(suggestion: "x"), withSuggestion: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("x --> <!--")]
    [InlineData("")]
    public void Marker_RejectsUnsafeFingerprints(string fingerprint)
    {
        Assert.Throws<ArgumentException>(() => CommentFormatter.Marker(fingerprint));
    }

    [Fact]
    public void ParseFingerprint_IgnoresMarkerNotAtEnd()
    {
        Assert.Null(CommentFormatter.ParseFingerprint("<!-- reviewer:fp=abc -->\nlater text"));
    }

    [Fact]
    public void ReviewBody_TruncatesToGitHubLimit()
    {
        var body = CommentFormatter.ReviewBody(new string('x', 70_000), []);

        Assert.True(body.Length < 65_536);
        Assert.EndsWith("_(truncated)_", body, StringComparison.Ordinal);
    }
}

public class GitHubUrlsTests
{
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
