namespace Reviewer.Agents.Api.Tests;

public sealed class WorktreeToolsTests : IDisposable
{
    private readonly Workspace _space = new();

    public void Dispose() => _space.Dispose();

    private WorktreeTools Tools(FakeGit? git = null) => new(_space.Worktree, git);

    [Fact]
    public void ReadFile_NumbersTheLines_InTheRangeAsked()
    {
        _space.Write("src/a.cs", "one\ntwo\nthree\nfour\n");

        var answer = Tools().ReadFile("src/a.cs", startLine: 2, lineCount: 2);

        Assert.Equal("src/a.cs, lines 2-3 of 4:\n     2\ttwo\n     3\tthree\n", answer);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("~/.ssh/id_ed25519")]
    [InlineData(".git/config")]
    [InlineData("src/.git/HEAD")]
    [InlineData("")]
    public void Paths_OutsideTheWorktree_AreRefused(string path)
    {
        File.WriteAllText(Path.Combine(_space.Root, "outside.txt"), "secret");

        var answer = Tools().ReadFile(path);

        Assert.Contains("isn't a path inside the checkout", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolicLinks_AreNotFollowed_ByAnyTool()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_space.Root, "home"));
        File.WriteAllText(Path.Combine(outside.FullName, "token"), "ghp_secret");
        _space.Write("src/a.cs", "fine");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_space.Worktree, "src", "token"), Path.Combine(outside.FullName, "token"));
            Directory.CreateSymbolicLink(Path.Combine(_space.Worktree, "home"), outside.FullName);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            return; // Windows creates links only with Developer Mode or admin rights, which CI runners lack.
        }

        var tools = Tools();

        Assert.Equal("src/token goes through a symbolic link, which isn't followed.", tools.ReadFile("src/token"));
        Assert.Equal("home/token goes through a symbolic link, which isn't followed.", tools.ReadFile("home/token"));
        Assert.Contains("symbolic link", tools.ListDir("home"), StringComparison.Ordinal);
        Assert.Contains("token@ (symbolic link, not followed)", tools.ListDir("src"), StringComparison.Ordinal);
        Assert.Equal("No matches.", tools.Grep("ghp_"));
    }

    [Fact]
    public void ListDir_MarksDirectories_AndHidesGit()
    {
        _space.Write("src/a.cs", "a");
        _space.Write("README.md", "r");
        Directory.CreateDirectory(Path.Combine(_space.Worktree, ".git"));

        Assert.Equal("README.md\nsrc/", Tools().ListDir("."));
        Assert.Equal("a.cs", Tools().ListDir("src"));
    }

    [Fact]
    public void Grep_FindsLines_AndSkipsBinaryFilesAndGit()
    {
        _space.Write("src/a.cs", "var total = 1;\nvar other = 2;\n");
        _space.Write("src/b.cs", "// total here too\n");
        File.WriteAllBytes(Path.Combine(_space.Worktree, "src", "c.bin"), [(byte)'t', (byte)'o', (byte)'t', (byte)'a', (byte)'l', 0]);
        _space.Write(".git/config", "total = secret\n");

        var answer = Tools().Grep("total");

        Assert.Equal(["src/a.cs:1: var total = 1;", "src/b.cs:1: // total here too"], answer.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Grep_StopsAtTheMatchCap_AndRefusesBadPatterns()
    {
        _space.Write("big.txt", string.Concat(Enumerable.Repeat("hit\n", 500)));
        var tools = Tools();

        var answer = tools.Grep("hit");

        Assert.Equal(WorktreeTools.MaxGrepMatches + 1, answer.Split('\n').Length);
        Assert.EndsWith("narrow the pattern or the path)", answer, StringComparison.Ordinal);
        Assert.StartsWith("Not a valid regular expression", tools.Grep("("), StringComparison.Ordinal);
    }

    [Fact]
    public void Answers_AreCapped()
    {
        _space.Write("wide.txt", string.Concat(Enumerable.Repeat(new string('x', 200) + "\n", 900)));

        var answer = Tools().ReadFile("wide.txt", lineCount: 900);

        Assert.True(answer.Length < WorktreeTools.MaxAnswerChars + 100);
        Assert.EndsWith("ask for less)", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitShow_ReadsAFileAtACommit_AndChecksItsArguments()
    {
        var git = new FakeGit(new() { ["abc1234:src/a.cs"] = "old\nlines" });
        var tools = Tools(git);

        Assert.Equal("     1\told\n     2\tlines\n", await tools.GitShowAsync("abc1234", "./src/a.cs"));
        Assert.Equal("src/b.cs doesn't exist at abc1234.", await tools.GitShowAsync("abc1234", "src/b.cs"));
        Assert.StartsWith("revision must be a commit SHA", await tools.GitShowAsync("HEAD~1", "src/a.cs"), StringComparison.Ordinal);
        Assert.StartsWith("revision must be a commit SHA", await tools.GitShowAsync("--output=/tmp/x", "src/a.cs"), StringComparison.Ordinal);
        Assert.Contains("isn't a path inside the repository", await tools.GitShowAsync("abc1234", "../x"), StringComparison.Ordinal);
        Assert.Contains("isn't a path inside the repository", await tools.GitShowAsync("abc1234", "-p"), StringComparison.Ordinal);
        Assert.Equal(["abc1234:src/a.cs", "abc1234:src/b.cs"], git.Reads);
    }

    [Fact]
    public void GitShow_IsOffered_OnlyWithARepo()
    {
        Assert.DoesNotContain(Tools().All, t => t.Name == "git_show");
        Assert.Equal(["read_file", "list_dir", "grep", "git_show"], Tools(new FakeGit([])).All.Select(t => t.Name));
    }
}
