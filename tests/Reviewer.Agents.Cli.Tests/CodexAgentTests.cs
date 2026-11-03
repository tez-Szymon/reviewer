using System.Text.Json;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;

namespace Reviewer.Agents.Cli.Tests;

public sealed class CodexAgentTests : IDisposable
{
    private const string ValidReport = """{"summary":"ok","findings":[{"id":"f1","severity":"major","category":"correctness","confidence":0.9,"file":"src/a.cs","startLine":3,"endLine":3,"title":"t","body":"b","suggestion":null}]}""";
    private const string InvalidReport = """{"summary":"ok","findings":[{"id":"f1","severity":"critical","category":"correctness","confidence":0.9,"file":"src/a.cs","startLine":3,"endLine":3,"title":"t","body":"b","suggestion":null}]}""";
    private const string Completed = """
        {"type":"thread.started","thread_id":"t1"}
        {"type":"turn.started"}
        {"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":5}}
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("reviewer-codex-tests-").FullName;
    private readonly ReviewContext _context;

    public CodexAgentTests()
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktree")).FullName;
        var attempt = Directory.CreateDirectory(Path.Combine(_root, "job", "1")).FullName;
        File.WriteAllText(Path.Combine(attempt, "schema.json"), FindingsSchema.Json);
        _context = new ReviewContext(worktree, attempt, TimeSpan.FromSeconds(30));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    private static CodexAgent Agent(FakeRunner runner, string? model = null, TimeProvider? time = null) =>
        new(new CodexOptions(Model: model), runner, time);

    private static string Failed(string message) =>
        $$$"""
        {"type":"turn.started"}
        {"type":"error","message":"Reconnecting... 1/5 ({{{message}}})"}
        {"type":"turn.failed","error":{"message":{{{JsonSerializer.Serialize(message)}}}}}
        """;

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public async Task Check_ReportsReadyCli()
    {
        var health = await Agent(new FakeRunner().HealthyCodex()).CheckAsync(Ct);

        Assert.True(health.IsReady);
        Assert.Equal("0.162.1", health.Version);
    }

    [Fact]
    public async Task Check_RefusesCliOlderThanVerifiedSandbox()
    {
        var health = await Agent(new FakeRunner().On("--version", "codex-cli 0.98.0")).CheckAsync(Ct);

        Assert.True(health.IsInstalled);
        Assert.False(health.IsReady);
        Assert.Contains("older than 0.162.0", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_RefusesCliWithoutRequiredFlags()
    {
        var runner = new FakeRunner()
            .On("--version", "codex-cli 0.170.0")
            .On("exec", "  --output-schema\n  --output-last-message\n  --json\n");

        var health = await Agent(runner).CheckAsync(Ct);

        Assert.False(health.IsReady);
        Assert.Contains("--ignore-user-config", health.Problem, StringComparison.Ordinal);
        Assert.Equal(["exec", "--help"], runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task Check_ReportsLoggedOut()
    {
        var runner = new FakeRunner()
            .On("--version", "codex-cli 0.162.1")
            .On("exec", "--output-schema --output-last-message --ignore-user-config --ignore-rules --ephemeral --json")
            .On("login", "Not logged in", exitCode: 1);

        var health = await Agent(runner).CheckAsync(Ct);

        Assert.True(health.IsInstalled);
        Assert.False(health.IsLoggedIn);
        Assert.Contains("codex login", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ReportsMissingExecutable_WithRealProcessRunner()
    {
        var agent = new CodexAgent(new CodexOptions(Executable: "reviewer-no-such-codex"), new CliWrapProcessRunner());

        var health = await agent.CheckAsync(Ct);

        Assert.False(health.IsInstalled);
    }

    [Fact]
    public async Task Review_RunsConfinedInWorktree_AndReturnsLastMessage()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(ValidReport, Completed);

        var report = await Agent(runner, model: "gpt-5.4").ReviewAsync(_context, Ct);

        Assert.Equal("f1", Assert.Single(report.Findings).Id);
        var call = runner.Calls[^1];
        Assert.Equal(_context.WorktreePath, call.WorkingDirectory);
        var args = call.Arguments;
        Assert.Equal("exec", args[0]);
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("--ignore-rules", args);
        Assert.Contains("--ephemeral", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--dangerously", StringComparison.Ordinal));
        Assert.Contains("default_permissions=\"reviewer\"", args);
        Assert.Contains($"permissions.reviewer.filesystem={{ \":minimal\" = \"read\", '{_context.WorktreePath}' = \"read\", '{_context.AttemptFolder}' = \"read\" }}", args);
        Assert.Contains("permissions.reviewer.network.enabled=false", args);
        Assert.Contains("project_doc_max_bytes=0", args);
        Assert.Contains("web_search=\"disabled\"", args);
        Assert.Contains("shell_environment_policy.inherit=\"core\"", args);
        foreach (var feature in new[] { "apps", "plugins", "hooks", "browser_use", "computer_use", "memories" })
        {
            Assert.Contains(args.Select((a, i) => (a, i)), p => p.a == "--disable" && args[p.i + 1] == feature);
        }

        Assert.Equal("gpt-5.4", Value(args, "--model"));
        Assert.Equal("--", args[^2]);
        Assert.Contains(Path.Combine(_context.AttemptFolder, "delta.patch"), args[^1], StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_context.AttemptFolder, "codex-schema.json"), Value(args, "--output-schema"));
        Assert.Equal(Completed, await File.ReadAllTextAsync(Path.Combine(_context.AttemptFolder, "events.jsonl"), Ct));
        Assert.True(File.Exists(Path.Combine(_context.AttemptFolder, "agent.log")));
    }

    [Fact]
    public async Task Review_WritesStrictSchema_WithEveryPropertyRequired()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(ValidReport, Completed);

        await Agent(runner).ReviewAsync(_context, Ct);

        var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_context.AttemptFolder, "codex-schema.json"), Ct)).RootElement;
        Assert.False(schema.TryGetProperty("$schema", out _));
        var item = schema.GetProperty("properties").GetProperty("findings").GetProperty("items");
        var required = item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(item.GetProperty("properties").EnumerateObject().Select(p => p.Name), required);
        Assert.Contains("suggestion", required);
    }

    [Fact]
    public async Task Review_ChecksHealthOnlyOnce()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(ValidReport, Completed).OnCodexExec(ValidReport, Completed);
        var agent = Agent(runner);

        await agent.ReviewAsync(_context, Ct);
        await agent.ReviewAsync(_context, Ct);

        Assert.Equal(5, runner.Calls.Count);
    }

    [Fact]
    public async Task Review_IsRefused_WhenCliIsNotReady()
    {
        var runner = new FakeRunner().On("--version", "codex-cli 0.1.0");

        await Assert.ThrowsAsync<AgentUnavailableException>(() => Agent(runner).ReviewAsync(_context, Ct));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Review_ExtractsJsonFromFencedAnswer()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec("Here:\n```json\n" + ValidReport + "\n```", Completed);

        var report = await Agent(runner).ReviewAsync(_context, Ct);

        Assert.Single(report.Findings);
    }

    [Fact]
    public async Task Review_RepairsInvalidOutputOnce_ReadingOnlyTheAttemptFolder()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(InvalidReport, Completed).OnCodexExec(ValidReport, Completed);

        var report = await Agent(runner).ReviewAsync(_context, Ct);

        Assert.Equal(Severity.Major, Assert.Single(report.Findings).Severity);
        var repair = runner.Calls[^1];
        Assert.Equal(_context.AttemptFolder, repair.WorkingDirectory);
        Assert.Contains("--skip-git-repo-check", repair.Arguments);
        Assert.Contains($"permissions.reviewer.filesystem={{ \":minimal\" = \"read\", '{_context.AttemptFolder}' = \"read\" }}", repair.Arguments);
        Assert.Equal(Path.Combine(_context.AttemptFolder, "output-repair.json"), Value(repair.Arguments, "--output-last-message"));
        Assert.Contains("critical", await File.ReadAllTextAsync(Path.Combine(_context.AttemptFolder, "invalid-output.json"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_FailsWhenRepairIsStillInvalid()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(InvalidReport, Completed).OnCodexExec(InvalidReport, Completed);

        await Assert.ThrowsAsync<AgentOutputException>(() => Agent(runner).ReviewAsync(_context, Ct));
    }

    [Fact]
    public async Task Review_ReportsThrottling_WithRetryTime()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var runner = new FakeRunner().HealthyCodex()
            .OnCodexExec(null, Failed("You've hit your usage limit. Upgrade to Pro, or try again in 1 day 2 hours 5 minutes."), exitCode: 1);

        var error = await Assert.ThrowsAsync<AgentThrottledException>(() => Agent(runner, time: time).ReviewAsync(_context, Ct));

        Assert.Equal(time.GetUtcNow() + new TimeSpan(1, 2, 5, 0), error.RetryAt);
    }

    [Fact]
    public async Task Review_ReportsThrottling_FromStatus()
    {
        var runner = new FakeRunner().HealthyCodex()
            .OnCodexExec(null, Failed("unexpected status 429 Too Many Requests: Rate limit reached"), exitCode: 1);

        var error = await Assert.ThrowsAsync<AgentThrottledException>(() => Agent(runner).ReviewAsync(_context, Ct));

        Assert.Null(error.RetryAt);
    }

    [Fact]
    public async Task Review_ReportsAuthProblems()
    {
        var runner = new FakeRunner().HealthyCodex()
            .OnCodexExec(null, Failed("unexpected status 401 Unauthorized: Missing bearer or basic authentication in header"), exitCode: 1);

        await Assert.ThrowsAsync<AgentUnavailableException>(() => Agent(runner).ReviewAsync(_context, Ct));
    }

    [Fact]
    public async Task Review_ReportsCrashWithoutEvents()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(null, "", exitCode: 2, stderr: "Error: Unknown feature flag: hooks");

        var error = await Assert.ThrowsAsync<AgentFailedException>(() => Agent(runner).ReviewAsync(_context, Ct));

        Assert.Contains("Unknown feature flag", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_FailsWhenNoAnswerWasWritten()
    {
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(null, Completed);

        await Assert.ThrowsAsync<AgentFailedException>(() => Agent(runner).ReviewAsync(_context, Ct));
    }

    [Fact]
    public async Task Review_IgnoresStaleAnswerFromEarlierRun()
    {
        await File.WriteAllTextAsync(Path.Combine(_context.AttemptFolder, "output.json"), ValidReport, Ct);
        var runner = new FakeRunner().HealthyCodex().OnCodexExec(null, Completed);

        await Assert.ThrowsAsync<AgentFailedException>(() => Agent(runner).ReviewAsync(_context, Ct));
    }

    [Fact]
    public async Task Review_TimesOut()
    {
        var runner = new FakeRunner().HealthyCodex().On("exec", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        });

        var error = await Assert.ThrowsAsync<AgentFailedException>(() =>
            Agent(runner).ReviewAsync(_context with { Timeout = TimeSpan.FromMilliseconds(50) }, Ct));

        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_HidesCredentialsFromAgentEnvironment_ButKeepsItsOwn()
    {
        Environment.SetEnvironmentVariable("REVIEWER_TEST_GITHUB_TOKEN", "secret");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY_REVIEWER_TEST", "own");
        try
        {
            var runner = new FakeRunner().HealthyCodex().OnCodexExec(ValidReport, Completed);

            await Agent(runner).ReviewAsync(_context, Ct);

            var env = runner.Calls[^1].Environment;
            Assert.Null(env["REVIEWER_TEST_GITHUB_TOKEN"]);
            Assert.False(env.ContainsKey("OPENAI_API_KEY_REVIEWER_TEST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVIEWER_TEST_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY_REVIEWER_TEST", null);
        }
    }

    [Theory]
    [InlineData("""{"type":"turn.completed"}""", null)]
    [InlineData("""{"type":"error","message":"Reconnecting... 2/5 (x)"}""", null)]
    [InlineData("""{"type":"error","message":"stream disconnected"}""", "stream disconnected")]
    [InlineData("not json\n{\"type\":\"turn.failed\",\"error\":{\"message\":\"boom\"}}", "boom")]
    public void Failure_ReadsTheTurnError(string events, string? expected)
    {
        Assert.Equal(expected, CodexAgent.Failure(events));
    }

    [Fact]
    public void Isolation_RefusesPathsTomlCantQuote()
    {
        Assert.Throws<AgentFailedException>(() => CodexAgent.Isolation(["/tmp/it's"]));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
