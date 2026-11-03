namespace Reviewer.Agents.Cli.Tests;

/// <summary>Answers CLI calls in order of registration, matched by their first argument.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    private readonly Queue<(string FirstArg, Func<ProcessSpec, CancellationToken, Task<ProcessResult>> Respond)> _responses = new();

    public List<ProcessSpec> Calls { get; } = [];

    public FakeRunner On(string firstArg, string stdout, int exitCode = 0, string stderr = "") =>
        On(firstArg, (_, _) => Task.FromResult(new ProcessResult(exitCode, stdout, stderr)));

    public FakeRunner On(string firstArg, Func<ProcessSpec, CancellationToken, Task<ProcessResult>> respond)
    {
        _responses.Enqueue((firstArg, respond));
        return this;
    }

    /// <summary>A healthy CLI: version, help with the required flags, logged in.</summary>
    public FakeRunner Healthy() => this
        .On("--version", "2.1.312 (Claude Code)\n")
        .On("--help", "  --restricted  ...\n  --json-schema <schema>\n  --strict-mcp-config\n  --tools <tools...>\n")
        .On("auth", """{ "loggedIn": true, "authMethod": "claude.ai" }""");

    /// <summary>A healthy Codex CLI: version, exec help with the required flags, logged in.</summary>
    public FakeRunner HealthyCodex() => this
        .On("--version", "codex-cli 0.162.1\n")
        .On("exec", "  --output-schema <FILE>\n  --output-last-message <FILE>\n  --ignore-user-config\n  --ignore-rules\n  --ephemeral\n  --json\n")
        .On("login", "Logged in using ChatGPT\n");

    /// <summary>A <c>codex exec</c> run: writes <paramref name="lastMessage"/> where the CLI was told to, prints events.</summary>
    public FakeRunner OnCodexExec(string? lastMessage, string events = "", int exitCode = 0, string stderr = "") =>
        On("exec", async (spec, ct) =>
        {
            if (lastMessage is not null)
            {
                var file = spec.Arguments[spec.Arguments.ToList().IndexOf("--output-last-message") + 1];
                await File.WriteAllTextAsync(file, lastMessage, ct);
            }

            return new ProcessResult(exitCode, events, stderr);
        });

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Calls.Add(spec);
        var (firstArg, respond) = _responses.Count > 0
            ? _responses.Dequeue()
            : throw new InvalidOperationException($"Unexpected call: {string.Join(' ', spec.Arguments)}");
        Assert.Equal(firstArg, spec.Arguments[0]);
        return respond(spec, ct);
    }
}
