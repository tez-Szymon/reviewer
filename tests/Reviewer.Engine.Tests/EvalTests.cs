using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Engine.Config;
using Reviewer.Engine.Eval;
using Reviewer.Git;
using Reviewer.Git.Tests;
using Reviewer.Instructions;

namespace Reviewer.Engine.Tests;

/// <summary>The eval set: its cases, how a run reviews and scores them, and how runs are compared.</summary>
public sealed class EvalTests : IDisposable
{
    private readonly SourceRepo _source = new();

    public void Dispose() => _source.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private static readonly ReviewPolicy Policy = new(ReviewMode.Gate, DryRun: false, MinConfidence: 0.5, [], [], MaxInlineComments: 10);

    private static readonly InstructionSettings Instructions = new(true, [], [], RepoAgentFileMode.Ignore);

    private EvalRunner Runner(IReviewAgent agent) =>
        new(new ReviewerPaths(_source.NewDirectory("evals")), _ => agent, Policy, Instructions, TimeSpan.FromMinutes(1), TimeProvider.System);

    private static Finding At(string file, int start, int end, double confidence = 0.9, Severity severity = Severity.Major) =>
        new($"f{start}", severity, Category.Correctness, confidence, file, start, end, $"Problem at {start}", "Body.");

    private string Fixture(string id, string yaml, Dictionary<string, string>? before, Dictionary<string, string> after)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source.NewDirectory("cases"), id)).FullName;
        File.WriteAllText(Path.Combine(folder, EvalCase.FileName), yaml);
        foreach (var (side, files) in new[] { (EvalCaseLoader.BeforeFolder, before), (EvalCaseLoader.AfterFolder, after) })
        {
            foreach (var (path, content) in files ?? [])
            {
                var full = Path.Combine(folder, side, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
        }

        return folder;
    }

    [Fact]
    public async Task ShippedCases_AreValid_AndEachFixtureBugIsInItsFile()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Reviewer.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var cases = await EvalCaseLoader.LoadAsync(Path.Combine(root, "eval", "cases"), Ct);

        Assert.True(cases.Count >= 8);
        Assert.Contains(cases, c => c.Bugs.Count == 0);
        Assert.Contains(cases, c => c.Source is RemoteSource);
        foreach (var fixture in cases.Where(c => c.Source is FixtureSource))
        {
            var after = ((FixtureSource)fixture.Source).After;
            foreach (var bug in fixture.Bugs)
            {
                var file = Path.Combine(after, bug.File);
                Assert.True(File.Exists(file), $"{fixture.Id}: {bug.File} isn't in after/.");
                Assert.True(bug.EndLine <= File.ReadAllLines(file).Length, $"{fixture.Id}: {bug.Key} is past the end of the file.");
            }
        }
    }

    [Fact]
    public void Parse_ReadsAFixtureCase()
    {
        var folder = Fixture("pager", "", null, new() { ["a.cs"] = "x" });

        var parsed = EvalCaseLoader.Parse("pager", folder, """
            title: Page the export
            description: Reads 500 at a time.
            bugs:
              - file: src/Pager.cs
                lines: 11-12
                about: Drops the last page.
              - file: src\Other.cs
                lines: 7
                about: Another.
            """);

        Assert.Equal("Page the export", parsed.Title);
        Assert.Equal("Reads 500 at a time.", parsed.Description);
        Assert.Equal(new FixtureSource(null, Path.Combine(folder, "after")), parsed.Source);
        Assert.Equal(
            [new KnownBug("src/Pager.cs", 11, 12, "Drops the last page."), new KnownBug("src/Other.cs", 7, 7, "Another.")],
            parsed.Bugs);
        Assert.Equal(["src/Pager.cs:11-12", "src/Other.cs:7"], parsed.Bugs.Select(b => b.Key));
    }

    [Fact]
    public void Parse_ReadsARemoteCase()
    {
        var parsed = EvalCaseLoader.Parse("real", _source.Root, $"""
            title: Real change
            repo: https://github.com/acme/app.git
            base: {new string('A', 40)}
            head: {new string('b', 40)}
            bugs: []
            """);

        Assert.Equal(new RemoteSource("https://github.com/acme/app.git", new string('a', 40), new string('b', 40)), parsed.Source);
        Assert.Empty(parsed.Bugs);
    }

    [Fact]
    public void Parse_ListsEveryProblem()
    {
        var error = Assert.Throws<ConfigException>(() => EvalCaseLoader.Parse("bad", _source.Root, """
            repo: https://github.com/acme/app.git
            head: abc123
            colour: red
            bugs:
              - file: a.cs
                lines: 9-3
                about: Backwards.
              - file: b.cs
                lines: soon
                about: Not a line.
              - lines: 4
            """));

        Assert.Equal(
            [
                "unknown key 'colour'.",
                "'title' is required.",
                "a remote case needs all of 'repo', 'base' and 'head'.",
                "'bugs[0].lines' must start at 1 or later and not end before it starts.",
                "'bugs[1].lines' must be a line or a range such as 41-44.",
                "'bugs[2]' needs 'file' and 'about'.",
            ],
            error.Errors.Select(e => e[(e.IndexOf(": ", StringComparison.Ordinal) + 2)..]));
    }

    [Fact]
    public void Parse_NeedsTheAfterFolder_OrARemote()
    {
        var error = Assert.Throws<ConfigException>(() => EvalCaseLoader.Parse("empty", _source.Root, "title: Nothing"));

        Assert.Contains("an after/ folder", Assert.Single(error.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_SkipsFoldersWithoutACase_AndRefusesAnEmptySet()
    {
        var cases = _source.NewDirectory("cases");
        Directory.CreateDirectory(Path.Combine(cases, "notes"));
        await Assert.ThrowsAsync<ConfigException>(() => EvalCaseLoader.LoadAsync(cases, Ct));

        Fixture("b-case", "title: B", null, new() { ["a.txt"] = "a" });
        Fixture("a-case", "title: A", null, new() { ["a.txt"] = "a" });

        Assert.Equal(["a-case", "b-case"], (await EvalCaseLoader.LoadAsync(cases, Ct)).Select(c => c.Id));
    }

    [Theory]
    [InlineData("src/Pager.cs", 8, 8, true)]    // three lines before the bug
    [InlineData("src/Pager.cs", 7, 7, false)]
    [InlineData("src/Pager.cs", 15, 20, true)]  // three lines after it
    [InlineData("src/Pager.cs", 16, 20, false)]
    [InlineData("src/Pager.cs", 1, 40, true)]   // around it
    [InlineData("./src/Pager.cs", 12, 12, true)]
    [InlineData("src/Other.cs", 12, 12, false)]
    public void Score_MatchesFindingsNearTheBugInItsFile(string file, int start, int end, bool caught)
    {
        var (bugs, other) = EvalRunner.Score([new KnownBug("src/Pager.cs", 11, 12, "Drops the last page.")], [At(file, start, end)]);

        Assert.Equal(caught, Assert.Single(bugs).Caught);
        Assert.Equal(caught ? 0 : 1, other.Count);
    }

    [Fact]
    public void Score_CreditsTheMostSevereFinding()
    {
        var minor = At("a.cs", 5, 5, severity: Severity.Minor);
        var blocker = At("a.cs", 6, 6, severity: Severity.Blocker);

        var (bugs, other) = EvalRunner.Score([new KnownBug("a.cs", 5, 6, "Bug.")], [minor, blocker]);

        Assert.Same(blocker, Assert.Single(bugs).CaughtBy);
        Assert.Empty(other);
    }

    [Fact]
    public async Task Run_ReviewsAFixture_AndScoresWhatTheReviewWouldPost()
    {
        var folder = Fixture(
            "pager",
            "",
            new() { ["src/Pager.cs"] = "one\ntwo\n", ["src/Old.cs"] = "old\n" },
            new() { ["src/Pager.cs"] = "one\ntwo\nthree\nfour\n", ["src/Old.cs"] = "old\n", ["src/New.cs"] = "new\n" });
        var evalCase = EvalCaseLoader.Parse("pager", folder, """
            title: Page it
            bugs:
              - file: src/Pager.cs
                lines: 3
                about: Three is wrong.
              - file: src/New.cs
                lines: 1
                about: So is new.
            """);
        var agent = new FakeAgent(new FindingsReport("Looked.",
        [
            At("src/Pager.cs", 3, 4),
            At("src/Old.cs", 1, 1),                    // outside the change: in the summary, still counted
            At("src/New.cs", 1, 1, confidence: 0.2),   // below the policy's confidence: dropped
        ]));

        var result = await Runner(agent).RunAsync(evalCase, Ct);

        Assert.Null(result.Error);
        Assert.Equal([true, false], result.Bugs.Select(b => b.Caught));
        Assert.Equal(1, result.Dropped);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal("src/Old.cs", Assert.Single(result.Other).File);
        var patch = await File.ReadAllTextAsync(Path.Combine(result.AttemptFolder!, ReviewContextBuilder.DeltaFile), Ct);
        Assert.Contains("+three", patch, StringComparison.Ordinal);
        Assert.Contains("+new", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("+one", patch, StringComparison.Ordinal);
        var context = await File.ReadAllTextAsync(Path.Combine(result.AttemptFolder!, ReviewContextBuilder.ContextFile), Ct);
        Assert.Contains("\"title\": \"Page it\"", context, StringComparison.Ordinal);
        Assert.False(Directory.Exists(agent.Worktree), "The worktree is removed after the review.");

        // The same files give the same commits, so a second run reviews exactly the same change.
        var again = await Runner(agent).RunAsync(evalCase, Ct);
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(again.AttemptFolder!, ReviewContextBuilder.ContextFile), Ct),
            context);
    }

    [Fact]
    public async Task Run_FetchesARemoteCasesCommits()
    {
        _source.Write("app.txt", "one");
        var baseSha = _source.Commit("base");
        _source.Write("app.txt", "one", "two");
        var headSha = _source.Commit("head");
        _source.Write("app.txt", "later");
        _source.Commit("later");
        var evalCase = new EvalCase(
            "remote", "Add two", "", new RemoteSource(_source.Url, baseSha, headSha), [new KnownBug("app.txt", 2, 2, "Two.")]);
        var agent = new FakeAgent(new FindingsReport("", [At("app.txt", 2, 2)]));

        var result = await Runner(agent).RunAsync(evalCase, Ct);

        Assert.Null(result.Error);
        Assert.True(Assert.Single(result.Bugs).Caught);
        Assert.Equal("one\ntwo\n", agent.Seen["app.txt"]);
    }

    [Fact]
    public async Task Run_ThatFails_SaysWhy_AndMissesEveryBug()
    {
        var folder = Fixture("broken", "", null, new() { ["a.txt"] = "a\n" });
        var evalCase = EvalCaseLoader.Parse("broken", folder, "title: Broken\nbugs:\n  - file: a.txt\n    lines: 1\n    about: A.");

        var result = await Runner(new FakeAgent(null)).RunAsync(evalCase, Ct);

        Assert.Equal("boom", result.Error);
        Assert.False(Assert.Single(result.Bugs).Caught);
        Assert.NotNull(result.AttemptFolder);
    }

    [Fact]
    public async Task Run_StopsOnAUsageLimit()
    {
        var folder = Fixture("limited", "", null, new() { ["a.txt"] = "a\n" });
        var evalCase = EvalCaseLoader.Parse("limited", folder, "title: Limited");

        await Assert.ThrowsAsync<AgentThrottledException>(() =>
            Runner(new FakeAgent(null, new AgentThrottledException("Usage limit.", null))).RunAsync(evalCase, Ct));
    }

    [Fact]
    public async Task Results_AreComparedWithTheLatestEarlierRunOfTheSameAgentAndModel()
    {
        var folder = _source.NewDirectory("results");
        var at = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        static EvalCaseRecord Case(string id, string[] caught, string[] missed, int other = 0, string? error = null) =>
            new(id, caught, missed, [.. Enumerable.Repeat(new EvalFindingRecord("minor", 0.9, "a.cs", 1, 1, "Nit."), other)], 0, 1, null, error);

        await EvalResults.SaveAsync(folder, new EvalRun("claude-code", null, "1.0", at.AddDays(-2), true, [Case("a", [], ["a.cs:1"])]), Ct);
        await EvalResults.SaveAsync(folder, new EvalRun("claude-code", "opus", "1.0", at.AddDays(-1), true, [Case("a", ["a.cs:1"], [])]), Ct);
        await EvalResults.SaveAsync(folder, new EvalRun("codex", null, "1.0", at.AddHours(-1), true, [Case("a", ["a.cs:1"], [])]), Ct);
        var previous = new EvalRun("claude-code", null, "1.0", at.AddHours(-2), true,
            [Case("a", ["a.cs:1"], ["a.cs:9"], other: 2), Case("b", ["b.cs:1"], []), Case("c", [], ["c.cs:1"])]);
        await EvalResults.SaveAsync(folder, previous, Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.json"), "{ not json", Ct);
        var run = new EvalRun("claude-code", null, "1.1", at, false,
            [Case("a", ["a.cs:9"], ["a.cs:1"], other: 1), Case("b", [], ["b.cs:1"], error: "boom"), Case("new", ["n.cs:1"], [])]);

        var found = await EvalResults.FindPreviousAsync(folder, run, Ct);
        var saved = await EvalResults.SaveAsync(folder, run, Ct);

        Assert.Equal(previous.Started, found!.Started);
        Assert.Equal("20261105-120000-claude-code.json", Path.GetFileName(saved));
        var comparison = EvalResults.Compare(found, run);
        // Only "a": "b" failed this time, "c" didn't run, "new" didn't run before.
        Assert.Equal((1, 1, 2, 2, 1), (comparison.PreviousCaught, comparison.Caught, comparison.Bugs, comparison.PreviousOther, comparison.Other));
        Assert.Equal(["a: a.cs:9"], comparison.NowCaught);
        Assert.Equal(["a: a.cs:1"], comparison.NowMissed);
        Assert.Equal((4, 2, 1, 1), (run.Bugs, run.Caught, run.Other, run.Failed));
    }

    private sealed class FakeAgent(FindingsReport? report, Exception? error = null) : IReviewAgent
    {
        public string? Worktree { get; private set; }

        public Dictionary<string, string> Seen { get; } = [];

        public string Id => "fake";

        public AgentCapabilities Capabilities { get; } = new(true, 1, true);

        public Task<AgentHealth> CheckAsync(CancellationToken ct) => Task.FromResult(new AgentHealth(true, true, "1"));

        public async Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct)
        {
            Worktree = context.WorktreePath;
            foreach (var file in Directory.EnumerateFiles(context.WorktreePath, "*.txt"))
            {
                Seen[Path.GetFileName(file)] = (await File.ReadAllTextAsync(file, ct)).ReplaceLineEndings("\n");
            }

            return report ?? throw error ?? new AgentFailedException("boom");
        }
    }
}
