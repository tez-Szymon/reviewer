namespace Reviewer.Git.Tests;

public class DiffParserTests
{
    [Fact]
    public void ParseNameStatus_ReturnsNewPaths_AndSkipsDeletions()
    {
        const string output = "M\0edit.txt\0D\0gone.txt\0R087\0old.txt\0new.txt\0A\0added.txt\0C100\0src.txt\0copy.txt\0";

        Assert.Equal(["edit.txt", "new.txt", "added.txt", "copy.txt"], DiffParser.ParseNameStatus(output));
    }

    [Fact]
    public void ParseChangedLines_ReadsHunkHeaders()
    {
        const string patch = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -2 +2 @@
            -old
            +new
            @@ -5,0 +6,3 @@ context
            +a
            +b
            +c
            @@ -9,2 +11,0 @@
            -x
            -y
            """;

        var lines = DiffParser.ParseChangedLines(patch);

        Assert.Equal([new LineSpan(2, 2), new LineSpan(6, 8), new LineSpan(12, 12)], lines["f.txt"]);
    }

    [Fact]
    public void ParseChangedLines_SkipsDeletedFiles()
    {
        const string patch = """
            --- a/gone.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -a
            -b
            """;

        Assert.Empty(DiffParser.ParseChangedLines(patch));
    }

    [Theory]
    [InlineData("+++ \"b/tab\\there.txt\"", "tab\there.txt")]
    [InlineData("+++ \"b/quote\\\"d.txt\"", "quote\"d.txt")]
    [InlineData("+++ \"b/caf\\303\\251.txt\"", "café.txt")]
    [InlineData("+++ b/with space.txt\t", "with space.txt")]
    public void ParseChangedLines_DecodesQuotedPaths(string header, string expected)
    {
        var lines = DiffParser.ParseChangedLines($"{header}\n@@ -1 +1 @@\n");

        Assert.Equal([new LineSpan(1, 1)], lines[expected]);
    }

    [Fact]
    public void ParseChangedLines_RejectsMalformedHunkHeader()
    {
        Assert.Throws<GitException>(() => DiffParser.ParseChangedLines("+++ b/f.txt\n@@ garbage @@\n"));
    }
}
