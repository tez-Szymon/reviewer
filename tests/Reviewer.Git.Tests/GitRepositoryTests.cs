namespace Reviewer.Git.Tests;

public sealed class GitRepositoryTests : IDisposable
{
    private const string HeadRef = "refs/reviewer/head";

    private readonly SourceRepo _source = new();

    public void Dispose() => _source.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<GitRepository> CacheAsync(params string[] refspecs)
    {
        var repo = await GitRepository.OpenOrInitAsync(_source.NewDirectory("cache.git"), Ct);
        await repo.FetchAsync(_source.Url, refspecs.Length > 0 ? refspecs : [$"main:{HeadRef}"], null, Ct);
        return repo;
    }

    [Fact]
    public async Task OpenOrInit_IsIdempotent()
    {
        var path = _source.NewDirectory("cache.git");

        await GitRepository.OpenOrInitAsync(path, Ct);
        var again = await GitRepository.OpenOrInitAsync(path, Ct);

        Assert.Equal(path, again.Path);
    }

    [Fact]
    public async Task Worktree_ChecksOutFetchedCommit_AndIsRemoved()
    {
        _source.Write("src/app.txt", "hello");
        var sha = _source.Commit("first");
        var repo = await CacheAsync();
        var worktree = _source.NewDirectory("job/worktree");

        await repo.AddWorktreeAsync(sha, worktree, Ct);
        var content = await File.ReadAllTextAsync(Path.Combine(worktree, "src", "app.txt"), Ct);
        await repo.RemoveWorktreeAsync(worktree, Ct);

        Assert.Equal("hello\n", content);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task Worktree_DoesNotRunHooks_EvenFromGlobalConfig()
    {
        var hooks = Directory.CreateDirectory(_source.NewDirectory("evil-hooks")).FullName;
        var marker = _source.NewDirectory("hook-ran");
        var hook = Path.Combine(hooks, "post-checkout");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ntouch '{marker.Replace('\\', '/')}'\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await File.WriteAllTextAsync(_source.GlobalConfig, $"[core]\n\thooksPath = {hooks.Replace('\\', '/')}\n", Ct);
        _source.Write("a.txt", "a");
        var sha = _source.Commit("first");
        var repo = await CacheAsync();

        await repo.AddWorktreeAsync(sha, _source.NewDirectory("wt"), Ct);

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Fetch_FailsWithGitException_ForUnknownRef()
    {
        _source.Commit("first");

        var error = await Assert.ThrowsAsync<GitException>(() => CacheAsync("refs/heads/missing:refs/reviewer/x"));

        Assert.StartsWith("git fetch failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsAncestor_DistinguishesNormalPushFromRebase()
    {
        _source.Write("a.txt", "a");
        var baseSha = _source.Commit("base");
        _source.Git("switch", "--quiet", "-c", "feature");
        _source.Write("b.txt", "b");
        var reviewed = _source.Commit("feature 1");
        _source.Write("b.txt", "b", "more");
        var pushed = _source.Commit("feature 2");
        _source.Git("switch", "--quiet", "main");
        _source.Write("c.txt", "c");
        _source.Commit("main moves on");
        _source.Git("switch", "--quiet", "feature");
        _source.Git("rebase", "--quiet", "main");
        var rebased = _source.Head();
        var repo = await CacheAsync($"main:{HeadRef}", "feature:refs/reviewer/feature", $"{pushed}:refs/reviewer/pushed");

        Assert.True(await repo.IsAncestorAsync(reviewed, pushed, Ct));
        Assert.False(await repo.IsAncestorAsync(reviewed, rebased, Ct));
        Assert.True(await repo.IsAncestorAsync(baseSha, rebased, Ct));
        await Assert.ThrowsAsync<GitException>(() => repo.IsAncestorAsync("0000000000000000000000000000000000000000", rebased, Ct));
    }

    [Fact]
    public async Task MergeBase_FindsForkPoint()
    {
        _source.Write("a.txt", "a");
        var fork = _source.Commit("base");
        _source.Git("switch", "--quiet", "-c", "feature");
        _source.Write("b.txt", "b");
        var feature = _source.Commit("feature");
        _source.Git("switch", "--quiet", "main");
        _source.Write("c.txt", "c");
        var main = _source.Commit("main");
        var repo = await CacheAsync($"main:{HeadRef}", "feature:refs/reviewer/feature");

        Assert.Equal(fork, await repo.MergeBaseAsync(main, feature, Ct));
    }

    [Fact]
    public async Task Diff_ReturnsUnifiedPatch()
    {
        _source.Write("a.txt", "one");
        var from = _source.Commit("first");
        _source.Write("a.txt", "two");
        var to = _source.Commit("second");
        var repo = await CacheAsync();

        var diff = await repo.DiffAsync(from, to, Ct);

        Assert.Contains("-one\n+two\n", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedFiles_ReportsNewSideLinesPerFile()
    {
        _source.Write("edit.txt", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10");
        _source.Write("delete-lines.txt", "a", "b", "c", "d");
        _source.Write("gone.txt", "bye");
        _source.Write("old-name.txt", "same", "content", "here", "so", "rename", "is", "detected");
        var from = _source.Commit("first");

        _source.Write("edit.txt", "1", "TWO", "3", "4", "5", "6", "7", "8", "NINE", "9b", "10");
        _source.Write("delete-lines.txt", "a", "d");
        File.Delete(Path.Combine(_source.Path, "gone.txt"));
        _source.Git("mv", "old-name.txt", "new-name.txt");
        _source.Write("new dir/new file.txt", "x", "y");
        _source.Write("zażółć.txt", "ü");
        var to = _source.Commit("second");
        var repo = await CacheAsync();

        var files = (await repo.ChangedFilesAsync(from, to, Ct)).ToDictionary(f => f.Path, f => f.Lines);

        Assert.Equal(["delete-lines.txt", "edit.txt", "new dir/new file.txt", "new-name.txt", "zażółć.txt"], files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([new LineSpan(2, 2), new LineSpan(9, 10)], files["edit.txt"]);
        Assert.Equal([new LineSpan(2, 2)], files["delete-lines.txt"]);
        Assert.Equal([new LineSpan(1, 2)], files["new dir/new file.txt"]);
        Assert.Empty(files["new-name.txt"]);
        Assert.Equal([new LineSpan(1, 1)], files["zażółć.txt"]);
    }

    [Fact]
    public async Task PatchIds_MatchSameChangeAcrossRebase()
    {
        _source.Write("a.txt", "a");
        _source.Commit("base");
        _source.Git("switch", "--quiet", "-c", "feature");
        _source.Write("b.txt", "b");
        var original = _source.Commit("feature change");
        _source.Commit("empty commit");
        var beforeRebase = _source.Head();
        _source.Git("switch", "--quiet", "main");
        _source.Write("c.txt", "c");
        _source.Commit("main moves on");
        _source.Git("switch", "--quiet", "feature");
        _source.Git("rebase", "--quiet", "--keep-empty", "main");
        var rebasedChange = _source.Git("rev-parse", "HEAD~1").Trim();
        var repo = await CacheAsync($"main:{HeadRef}", "feature:refs/reviewer/feature", $"{beforeRebase}:refs/reviewer/old");

        var before = await repo.PatchIdsAsync($"{HeadRef}~1..{beforeRebase}", Ct);
        var after = await repo.PatchIdsAsync($"{HeadRef}..refs/reviewer/feature", Ct);

        Assert.NotEqual(original, rebasedChange);
        Assert.Equal(before[original], after[rebasedChange]);
        Assert.Single(before);
    }

    [Fact]
    public async Task ReadFile_ReturnsContentAtCommit_OrNullWhenMissing()
    {
        _source.Write("docs/CLAUDE.md", "old");
        var first = _source.Commit("first");
        _source.Write("docs/CLAUDE.md", "new");
        _source.Commit("second");
        var repo = await CacheAsync();

        var content = await repo.ReadFileAsync(first, "docs/CLAUDE.md", Ct);
        var missing = await repo.ReadFileAsync(first, "AGENTS.md", Ct);

        Assert.Equal("old\n", content);
        Assert.Null(missing);
    }

    [Fact]
    public async Task CredentialEnvironment_AnswersGitWithCredential_AndReplacesUserHelpers()
    {
        await File.WriteAllTextAsync(_source.GlobalConfig, "[credential]\n\thelper = \"!echo username=leaked; echo password=keychain #\"\n", Ct);

        var filled = Fill(GitRepository.CredentialEnvironment(GitHubRemote, new GitCredential("x-access-token", "s3cret")));
        var none = Fill(GitRepository.CredentialEnvironment(GitHubRemote, null));

        Assert.Contains("username=x-access-token\n", filled, StringComparison.Ordinal);
        Assert.Contains("password=s3cret\n", filled, StringComparison.Ordinal);
        Assert.DoesNotContain("keychain", none, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialEnvironment_AnswersOnlyForTheRemotesHost()
    {
        var environment = GitRepository.CredentialEnvironment(GitHubRemote, new GitCredential("x-access-token", "s3cret"));

        Assert.DoesNotContain("s3cret", Fill(environment, host: "evil.example"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialEnvironment_SwitchesOffAskpassPrograms()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The askpass stand-in is a shell script.
        }

        var askpass = _source.NewDirectory("askpass.sh");
        await File.WriteAllTextAsync(askpass, "#!/bin/sh\necho from-askpass\n", Ct);
        File.SetUnixFileMode(askpass, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(_source.GlobalConfig, $"[core]\n\taskPass = {askpass}\n", Ct);
        var userEnvironment = new Dictionary<string, string?> { ["GIT_ASKPASS"] = askpass, ["SSH_ASKPASS"] = askpass };

        var engine = new Dictionary<string, string?>(userEnvironment);
        foreach (var (key, value) in GitRepository.CredentialEnvironment(GitHubRemote, null))
        {
            engine[key] = value; // The engine's settings replace the user's, as they do for a fetch.
        }

        var output = Fill(engine);

        Assert.DoesNotContain("from-askpass", output, StringComparison.Ordinal);
        Assert.Contains("from-askpass", Fill(userEnvironment), StringComparison.Ordinal); // The stand-in does work.
    }

    [Fact]
    public void Credential_ToString_HidesPassword()
    {
        Assert.DoesNotContain("s3cret", new GitCredential("u", "s3cret").ToString(), StringComparison.Ordinal);
    }

    // "git credential fill" asks the configured helpers exactly as a fetch would. With no answer it fails (prompts are
    // off), and the empty string stands for that.
    private const string GitHubRemote = "https://github.com/acme/app.git";

    private string Fill(IReadOnlyDictionary<string, string?> environment, string host = "github.com")
    {
        var start = new System.Diagnostics.ProcessStartInfo("git", ["credential", "fill"])
        {
            WorkingDirectory = _source.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Write($"protocol=https\nhost={host}\n\n");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.ReplaceLineEndings("\n") : "";
    }

    [Fact]
    public async Task PathLimitedDiff_KeepsOnlyThosePaths_AndTreatsThemLiterally()
    {
        _source.Write("a.txt", "a");
        _source.Write("[ab].txt", "glob");
        var from = _source.Commit("base");
        _source.Write("a.txt", "a2"); // "[ab].txt" as a glob would match this.
        _source.Write("[ab].txt", "glob2");
        _source.Write("b.txt", "b");
        var to = _source.Commit("change");
        var repo = await CacheAsync();

        var changed = await repo.ChangedFilesAsync(from, to, Ct, ["[ab].txt", "b.txt"]);
        var diff = await repo.DiffAsync(from, to, Ct, ["[ab].txt"]);

        Assert.Equal(["[ab].txt", "b.txt"], changed.Select(f => f.Path));
        Assert.Contains("+glob2", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("a2", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commits_AreOldestFirst_HasCommitAndUpdateRefWork()
    {
        var first = _source.Commit("first");
        var second = _source.Commit("second");
        var third = _source.Commit("third");
        var repo = await CacheAsync();

        Assert.Equal([second, third], await repo.CommitsAsync($"{first}..{third}", Ct));
        Assert.True(await repo.HasCommitAsync(second, Ct));
        Assert.False(await repo.HasCommitAsync(new string('0', 40), Ct));

        await repo.UpdateRefAsync("refs/reviewer/pr/1/reviewed", second, Ct);
        Assert.Equal([second], await repo.CommitsAsync($"{first}..refs/reviewer/pr/1/reviewed", Ct));
    }

    [Fact]
    public async Task ChangedOldLines_MapToTheOlderRevision()
    {
        _source.Write("f.txt", "1", "2", "3", "4");
        var from = _source.Commit("base");
        _source.Write("f.txt", "0", "1", "2", "THREE", "4");
        var to = _source.Commit("change");
        var repo = await CacheAsync();

        var lines = await repo.ChangedOldLinesAsync(from, to, Ct);

        Assert.Equal([new LineSpan(1, 0), new LineSpan(3, 3)], lines["f.txt"]);
    }
}
