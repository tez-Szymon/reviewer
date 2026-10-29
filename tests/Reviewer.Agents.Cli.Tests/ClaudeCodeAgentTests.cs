using System.Text.Json;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;

namespace Reviewer.Agents.Cli.Tests;

public sealed class ClaudeCodeAgentTests : IDisposable
{
    private const string ValidReport = """{"summary":"ok","findings":[{"id":"f1","severity":"major","category":"correctness","confidence":0.9,"file":"src/a.cs","startLine":3,"endLine":3,"title":"t","body":"b"}]}""";
    private const string InvalidReport = """{"summary":"ok","findings":[{"id":"f1","severity":"critical","category":"correctness","confidence":0.9,"file":"src/a.cs","startLine":3,"endLine":3,"title":"t","body":"b"}]}""";

    private readonly string _root = Directory.CreateTempSubdirectory("reviewer-agent-tests-").FullName;
    private readonly ReviewContext _context;

    public ClaudeCodeAgentTests()
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktree")).FullName;
        var attempt = Directory.CreateDirectory(Path.Combine(_root, "job", "1")).FullName;
        File.WriteAllText(Path.Combine(attempt, "schema.json"), FindingsSchema.Json);
        _context = new ReviewContext(worktree, attempt, TimeSpan.FromSeconds(30));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    private static ClaudeCodeAgent Agent(FakeRunner runner, string? model = null) => new(new ClaudeCodeOptions(Model: model), runner);

    private static string Envelope(string? structured = null, string result = "", bool isError = false, int? apiStatus = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = isError ? "error_during_execution" : "success",
            ["is_error"] = isError,
            ["result"] = result,
            ["api_error_status"] = apiStatus,
            ["structured_output"] = structured is null ? null : JsonDocument.Parse(structured).RootElement,
        });

    [Fact]
    public async Task Check_ReportsReadyCli()
    {
        var health = await Agent(new FakeRunner().Healthy()).CheckAsync(Ct);

        Assert.True(health.IsReady);
        Assert.Equal("2.1.312", health.Version);
    }

    [Fact]
    public async Task Check_RefusesCliWithoutRestrictedMode()
    {
        var runner = new FakeRunner()
            .On("--version", "1.0.0 (Claude Code)")
            .On("--help", "  --json-schema\n  --strict-mcp-config\n  --tools\n");

        var health = await Agent(runner).CheckAsync(Ct);

        Assert.False(health.IsReady);
        Assert.Contains("--restricted", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ReportsLoggedOut()
    {
        var runner = new FakeRunner()
            .On("--version", "2.1.312 (Claude Code)")
            .On("--help", "--restricted --json-schema --strict-mcp-config --tools")
            .On("auth", """{ "loggedIn": false }""");

        var health = await Agent(runner).CheckAsync(Ct);

        Assert.True(health.IsInstalled);
        Assert.False(health.IsLoggedIn);
        Assert.Contains("claude auth login", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ReportsMissingExecutable_WithRealProcessRunner()
    {
        var agent = new ClaudeCodeAgent(new ClaudeCodeOptions(Executable: "reviewer-no-such-claude"), new CliWrapProcessRunner());

        var health = await agent.CheckAsync(Ct);

        Assert.False(health.IsInstalled);
    }

    [Fact]
    public async Task Review_RunsRestrictedReadOnly_InWorktree_AndReturnsStructuredOutput()
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(ValidReport));

        var report = await Agent(runner, model: "sonnet").ReviewAsync(_context, Ct);

        Assert.Equal("f1", Assert.Single(report.Findings).Id);
        var call = runner.Calls[^1];
        Assert.Equal(_context.WorktreePath, call.WorkingDirectory);
        var args = call.Arguments;
        Assert.Contains("--restricted", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("Read,Grep,Glob", args[args.ToList().IndexOf("--tools") + 1]);
        Assert.Equal(_context.AttemptFolder, args[args.ToList().IndexOf("--add-dir") + 1]);
        var schemaArg = JsonDocument.Parse(args[args.ToList().IndexOf("--json-schema") + 1]).RootElement;
        Assert.False(schemaArg.TryGetProperty("$schema", out _));
        Assert.Equal("FindingsReport", schemaArg.GetProperty("title").GetString());
        Assert.Equal("sonnet", args[args.ToList().IndexOf("--model") + 1]);
        Assert.Contains(Path.Combine(_context.AttemptFolder, "delta.patch"), args[1], StringComparison.Ordinal);
        Assert.Equal(Envelope(ValidReport), await File.ReadAllTextAsync(Path.Combine(_context.AttemptFolder, "output.json"), Ct));
        Assert.True(File.Exists(Path.Combine(_context.AttemptFolder, "agent.log")));
    }

    [Fact]
    public async Task Review_ChecksHealthOnlyOnce()
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(ValidReport)).On("-p", Envelope(ValidReport));
        var agent = Agent(runner);

        await agent.ReviewAsync(_context, Ct);
        await agent.ReviewAsync(_context, Ct);

        Assert.Equal(5, runner.Calls.Count);
    }

    [Fact]
    public async Task Review_IsRefused_WhenCliIsNotReady()
    {
        var runner = new FakeRunner().On("--version", "1.0.0").On("--help", "");

        await Assert.ThrowsAsync<AgentUnavailableException>(() => Agent(runner).ReviewAsync(_context, Ct));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData("Here you go:\n```json\n" + ValidReport + "\n```\nDone.")]
    [InlineData("Result: " + ValidReport)]
    public async Task Review_FallsBackToResultText_WithoutStructuredOutput(string result)
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(result: result));

        var report = await Agent(runner).ReviewAsync(_context, Ct);

        Assert.Single(report.Findings);
    }

    [Fact]
    public async Task Review_RepairsInvalidOutputOnce()
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(InvalidReport)).On("-p", Envelope(ValidReport));

        var report = await Agent(runner).ReviewAsync(_context, Ct);

        Assert.Equal(Severity.Major, Assert.Single(report.Findings).Severity);
        var repair = runner.Calls[^1];
        Assert.Equal(_context.AttemptFolder, repair.WorkingDirectory);
        Assert.Equal("Read", repair.Arguments[repair.Arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Contains("--restricted", repair.Arguments);
        Assert.Contains("critical", await File.ReadAllTextAsync(Path.Combine(_context.AttemptFolder, "invalid-output.json"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_FailsWhenRepairIsStillInvalid()
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(InvalidReport)).On("-p", Envelope(InvalidReport));

        await Assert.ThrowsAsync<AgentOutputException>(() => Agent(runner).ReviewAsync(_context, Ct));
    }

    [Fact]
    public async Task Review_ReportsThrottling_WithResetTime()
    {
        var runner = new FakeRunner().Healthy()
            .On("-p", Envelope(result: "Claude AI usage limit reached|1793300000", isError: true), exitCode: 1);

        var error = await Assert.ThrowsAsync<AgentThrottledException>(() => Agent(runner).ReviewAsync(_context, Ct));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793300000), error.RetryAt);
    }

    [Fact]
    public async Task Review_ReportsThrottling_FromApiStatus()
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(result: "API Error", isError: true, apiStatus: 429), exitCode: 1);

        var error = await Assert.ThrowsAsync<AgentThrottledException>(() => Agent(runner).ReviewAsync(_context, Ct));

        Assert.Null(error.RetryAt);
    }

    [Theory]
    [InlineData("Invalid API key · Please run /login", null)]
    [InlineData("API Error", 401)]
    public async Task Review_ReportsAuthProblems(string result, int? apiStatus)
    {
        var runner = new FakeRunner().Healthy().On("-p", Envelope(result: result, isError: true, apiStatus: apiStatus), exitCode: 1);

        await Assert.ThrowsAsync<AgentUnavailableException>(() => Agent(runner).ReviewAsync(_context, Ct));
    }

    [Fact]
    public async Task Review_ReportsCrashWithoutJson()
    {
        var runner = new FakeRunner().Healthy().On("-p", "", exitCode: 2, stderr: "Segmentation fault");

        var error = await Assert.ThrowsAsync<AgentFailedException>(() => Agent(runner).ReviewAsync(_context, Ct));

        Assert.Contains("Segmentation fault", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_TimesOut()
    {
        var runner = new FakeRunner().Healthy().On("-p", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        });

        var error = await Assert.ThrowsAsync<AgentFailedException>(() =>
            Agent(runner).ReviewAsync(_context with { Timeout = TimeSpan.FromMilliseconds(50) }, Ct));

        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner().Healthy().On("-p", async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Agent(runner).ReviewAsync(_context, cts.Token));
    }

    [Fact]
    public async Task Review_HidesCredentialsFromAgentEnvironment()
    {
        Environment.SetEnvironmentVariable("REVIEWER_TEST_GITHUB_TOKEN", "secret");
        try
        {
            var runner = new FakeRunner().Healthy().On("-p", Envelope(ValidReport));

            await Agent(runner).ReviewAsync(_context, Ct);

            var env = runner.Calls[^1].Environment;
            Assert.True(env.ContainsKey("REVIEWER_TEST_GITHUB_TOKEN"));
            Assert.Null(env["REVIEWER_TEST_GITHUB_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVIEWER_TEST_GITHUB_TOKEN", null);
        }
    }
}
