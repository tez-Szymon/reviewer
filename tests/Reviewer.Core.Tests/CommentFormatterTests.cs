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

    private static readonly FindingsReport Walkthrough = new(
        "Adds an appearance picker.",
        [],
        new ReviewRisk(RiskLevel.Low, "Desktop-only preference with a safe default."),
        [new ChangeArea("Preferences", "Saves `Appearance`"), new ChangeArea("Settings UI", "New picker")],
        ["Old files load as System", "Both settings are saved together"],
        ["Runtime visual behaviour", "Contrast in dark mode"]);

    [Fact]
    public void Headline_NoFindings_ShowsStatusRiskAndSummary()
    {
        Assert.Equal(
            "## ✅ No issues found · Risk: low\n\nAdds an appearance picker.\n\n**Risk:** Desktop-only preference with a safe default.",
            CommentFormatter.Headline(Walkthrough, [], reReview: false));
    }

    [Fact]
    public void Headline_CountsFindingsBySeverity_MostSevereFirst()
    {
        Finding Of(Severity severity) => Comment().Finding with { Severity = severity };

        var headline = CommentFormatter.Headline(new FindingsReport("s", []), [Of(Severity.Minor), Of(Severity.Major), Of(Severity.Minor)], reReview: true);

        Assert.Equal("## ⚠️ 3 new findings: 1 major, 2 minor\n\ns", headline);
    }

    [Fact]
    public void Details_RendersCollapsedSections_WithTheFirstGapInTheToggle()
    {
        var details = CommentFormatter.Details(Walkthrough, reReview: false);

        Assert.Equal("""
            <details>
            <summary><b>Changes</b> · 2 areas</summary>

            | Area | What changed |
            |---|---|
            | Preferences | Saves `Appearance` |
            | Settings UI | New picker |

            </details>

            <details>
            <summary><b>What I checked</b> (2)</summary>

            - Old files load as System
            - Both settings are saved together

            </details>

            <details>
            <summary><b>Not verified</b> (2): Runtime visual behaviour</summary>

            - Runtime visual behaviour
            - Contrast in dark mode

            </details>
            """, details);
    }

    [Fact]
    public void Details_LeavesOutEmptySections_AndNamesReReviewChanges()
    {
        var details = CommentFormatter.Details(Walkthrough with { Checked = [], NotVerified = null }, reReview: true);

        Assert.Contains("<b>Changes since the last review</b>", details, StringComparison.Ordinal);
        Assert.DoesNotContain("What I checked", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Not verified", details, StringComparison.Ordinal);
        Assert.Equal("", CommentFormatter.Details(new FindingsReport("s", []), reReview: false));
    }

    [Fact]
    public void Details_KeepsModelTextInsideItsSection()
    {
        var details = CommentFormatter.Details(new FindingsReport("s", [],
            Changes: [new ChangeArea("a|b", "line\nbreak </details> @bob")],
            NotVerified: ["<b>bold</b> " + new string('x', 200)]), reReview: false);

        Assert.Contains("| a\\|b | line break &lt;/details> @​bob |", details, StringComparison.Ordinal);
        Assert.Contains("(1): &lt;b&gt;bold&lt;/b&gt; xxx", details, StringComparison.Ordinal);
        Assert.Contains("x…</summary>", details, StringComparison.Ordinal);
        Assert.Equal(3, details.Split("</details>").Length);
    }

    [Fact]
    public void ReviewBody_PutsDetailsAfterTheListedFindings()
    {
        var body = CommentFormatter.ReviewBody("## ✅", [Comment().Finding], "<details>x</details>");

        Assert.True(body.IndexOf("### Other findings", StringComparison.Ordinal) < body.IndexOf("<details>", StringComparison.Ordinal));
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
