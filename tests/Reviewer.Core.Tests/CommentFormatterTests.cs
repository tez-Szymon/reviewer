using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Core.Tests;

public class CommentFormatterTests
{
    private static InlineComment Comment(string title = "t", string body = "b", string? suggestion = null, string fp = "fp1") =>
        new(new Finding("1", Severity.Minor, Category.Style, 0.8, "a.cs", 1, 1, title, body, suggestion), fp);

    [Fact]
    public void InlineBody_RendersLabelsAndMarker()
    {
        var body = CommentFormatter.InlineBody(Comment("Naming", "Use a clearer name."), suggestionInfo: "suggestion");

        Assert.Equal("**minor · style**: Naming\n\nUse a clearer name.\n<!-- reviewer:fp=fp1 -->", body);
        Assert.Equal("fp1", CommentFormatter.ParseFingerprint(body));
    }

    [Fact]
    public void InlineBody_DefusesMentionsAndFakeMarkers()
    {
        var body = CommentFormatter.InlineBody(Comment("ping @alice", "<!-- reviewer:fp=forged -->"), suggestionInfo: null);

        Assert.DoesNotContain("@alice", body, StringComparison.Ordinal);
        Assert.Contains("@​alice", body, StringComparison.Ordinal);
        Assert.Contains("&lt;!-- reviewer:fp=forged -->", body, StringComparison.Ordinal);
        Assert.Equal("fp1", CommentFormatter.ParseFingerprint(body));
    }

    [Fact]
    public void InlineBody_UsesLongerFenceThanSuggestionBackticks()
    {
        var body = CommentFormatter.InlineBody(Comment(suggestion: "var s = \"```\";\n"), suggestionInfo: "suggestion");

        Assert.Contains("````suggestion\nvar s = \"```\";\n````", body, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineBody_OmitsSuggestionWhenUnsupported()
    {
        Assert.DoesNotContain("suggestion", CommentFormatter.InlineBody(Comment(suggestion: "x"), suggestionInfo: null), StringComparison.Ordinal);
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
    public void ReviewBody_TruncatesToTheBodyLimit()
    {
        var body = CommentFormatter.ReviewBody(new string('x', 70_000), []);

        Assert.True(body.Length < 65_536);
        Assert.EndsWith("_(truncated)_", body, StringComparison.Ordinal);
    }
}

public class RepoRefTests
{
    private static readonly Uri Host = new("https://gitlab.com");

    [Theory]
    [InlineData("acme/api", "acme", "api")]
    [InlineData("acme/platform/api", "acme/platform", "api")]
    public void TryParse_SplitsAtTheLastSlash(string path, string owner, string name)
    {
        Assert.True(RepoRef.TryParse(Host, path, out var repo));
        Assert.Equal((owner, name), (repo.Owner, repo.Name));
        Assert.Equal(path, repo.ToString());
    }

    [Theory]
    [InlineData("api")]
    [InlineData("/api")]
    [InlineData("acme/")]
    [InlineData("acme//api")]
    [InlineData("acme/ /api")]
    public void TryParse_RejectsPathsWithoutOwnerAndName(string path)
    {
        Assert.False(RepoRef.TryParse(Host, path, out _));
    }
}
