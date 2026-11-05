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

    [Fact]
    public void ParseChangedLines_ContentLookingLikeHeader_IsNotAHeader()
    {
        const string patch = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,0 +2,2 @@
            +++ not a header
            +x
            """;

        Assert.Equal([new LineSpan(2, 3)], Assert.Single(DiffParser.ParseChangedLines(patch)).Value);
    }

    [Fact]
    public void ParseOldChangedLines_ReadsOldSide_InsertionsAsGaps_DeletedFilesWhole()
    {
        const string patch = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -2 +2 @@
            -old
            +new
            @@ -5,0 +6,2 @@
            +a
            +b
            @@ -9,2 +10,0 @@
            --- not a header
            -y
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            --- a/gone.txt
            +++ /dev/null
            @@ -1,3 +0,0 @@
            -a
            -b
            -c
            diff --git a/new.txt b/new.txt
            new file mode 100644
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1 @@
            +n
            """;

        var lines = DiffParser.ParseOldChangedLines(patch);

        Assert.Equal([new LineSpan(2, 2), new LineSpan(6, 5), new LineSpan(9, 10)], lines["f.txt"]);
        Assert.Equal([new LineSpan(1, int.MaxValue)], lines["gone.txt"]);
        Assert.Equal(2, lines.Count);
        Assert.False(new LineSpan(6, 5).Overlaps(5, 5)); // A gap after line 5 doesn't touch line 5 alone...
        Assert.True(new LineSpan(6, 5).Overlaps(4, 7)); // ...only a range spanning it.
    }

    [Fact]
    public void FilePatchIds_IgnoreLineNumbersAndWhitespace_ButNotContent()
    {
        static string Patch(string at, string added) => $"""
            diff --git a/f.cs b/f.cs
            index 1111111..2222222 100644
            --- a/f.cs
            +++ b/f.cs
            @@ {at} @@
            -old();
            +{added}
            """;

        var original = DiffParser.FilePatchIds(Patch("-3 +3", "next(1);"))["f.cs"];

        Assert.Equal(original, DiffParser.FilePatchIds(Patch("-40 +41", "next(1);")).Single().Value);
        Assert.Equal(original, DiffParser.FilePatchIds(Patch("-3 +3", "  next(1);  ")).Single().Value);
        Assert.NotEqual(original, DiffParser.FilePatchIds(Patch("-3 +3", "next(2);")).Single().Value);
    }

    [Fact]
    public void FilePatchIds_KeyRenamesByNewPath_AndDeletionsByOldPath()
    {
        const string patch = """
            diff --git a/old.txt b/new.txt
            similarity index 100%
            rename from old.txt
            rename to new.txt
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            index 3333333..0000000
            --- a/gone.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -x
            """;

        Assert.Equal(["new.txt", "gone.txt"], DiffParser.FilePatchIds(patch).Keys);
    }

    [Theory]
    [InlineData(2, 2, 2, 2)]   // Before every hunk: unchanged.
    [InlineData(5, 5, 5, 6)]   // Changed: maps to both lines that replaced it.
    [InlineData(8, 8, 11, 11)] // After the insertion after line 7: shifted by both hunks.
    [InlineData(7, 7, 8, 8)]   // The insertion is after line 7, so line 7 moves only by the first hunk.
    [InlineData(4, 9, 4, 12)]  // A range spanning both.
    public void MapSpan_ShiftsAndFollowsChangedLines(int start, int end, int newStart, int newEnd)
    {
        const string patch = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -5 +5,2 @@
            -five
            +FIVE
            +six-ish
            @@ -7,0 +9,2 @@
            +new
            +new
            """;

        Assert.Equal(new LineSpan(newStart, newEnd), DiffParser.MapSpan(patch, new LineSpan(start, end)));
    }

    [Fact]
    public void MapSpan_DeletedLines_MapToTheLineAfter_AndADeletedFileToNull()
    {
        const string deletion = "--- a/f.txt\n+++ b/f.txt\n@@ -3,2 +2,0 @@\n-a\n-b";
        const string deleted = "--- a/f.txt\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-a\n-b";

        Assert.Equal(new LineSpan(3, 3), DiffParser.MapSpan(deletion, new LineSpan(3, 4)));
        Assert.Equal(new LineSpan(4, 4), DiffParser.MapSpan(deletion, new LineSpan(6, 6)));
        Assert.Null(DiffParser.MapSpan(deleted, new LineSpan(1, 1)));
        Assert.Equal(new LineSpan(3, 3), DiffParser.MapSpan("", new LineSpan(3, 3)));
    }
}
