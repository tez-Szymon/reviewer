using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;

namespace Reviewer.Agents.Cli;

/// <param name="Executable">The <c>claude</c> binary, resolved from PATH by default.</param>
/// <param name="Model">Model alias or name; null uses the CLI's default for the subscription.</param>
public sealed record ClaudeCodeOptions(string Executable = "claude", string? Model = null);

/// <summary>
/// Runs Claude Code headless in the PR worktree under the user's own subscription.
/// </summary>
/// <remarks>
/// Isolation relies on <c>--restricted</c> (verified on 2.1.312): no code-running tools, user/project/local settings
/// ignored (so a PR can't add hooks via <c>.claude/settings.json</c>), the worktree's <c>CLAUDE.md</c> not loaded, and
/// file tools confined to the working directories. The agent gets read-only tools and no MCP servers. A CLI without
/// <c>--restricted</c> is refused rather than run unprotected.
/// </remarks>
public sealed partial class ClaudeCodeAgent(ClaudeCodeOptions options, IProcessRunner runner) : IReviewAgent
{
    public const string OutputFile = "output.json";
    public const string LogFile = "agent.log";

    private const string ReadOnlyTools = "Read,Grep,Glob";
    private const string InvalidOutputFile = "invalid-output.json";
    private const string RepairOutputFile = "output-repair.json";

    private static readonly string[] RequiredFlags = ["--restricted", "--json-schema", "--strict-mcp-config", "--tools"];

    // Set once a health check passes, so later reviews skip the three extra CLI calls.
    private bool _verified;

    public string Id => "claude-code";

    public AgentCapabilities Capabilities { get; } = new(NeedsWorktree: true, MaxConcurrency: 1, SupportsSchemaFlag: true);

    public async Task<AgentHealth> CheckAsync(CancellationToken ct)
    {
        var home = Environment.CurrentDirectory;
        ProcessResult version;
        try
        {
            version = await RunAsync(["--version"], home, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception || e.InnerException is Win32Exception)
        {
            return new AgentHealth(false, false, null, $"'{options.Executable}' could not be started: {e.Message}");
        }

        var versionText = version.StandardOutput.Trim().Split(' ').FirstOrDefault();
        if (version.ExitCode != 0 || string.IsNullOrEmpty(versionText))
        {
            return new AgentHealth(false, false, null, $"'{options.Executable} --version' failed: {version.StandardError.Trim()}");
        }

        var help = await RunAsync(["--help"], home, ct).ConfigureAwait(false);
        var missing = RequiredFlags.Where(f => !help.StandardOutput.Contains(f, StringComparison.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            return new AgentHealth(true, false, versionText,
                $"Claude Code {versionText} lacks {string.Join(", ", missing)}; update it to run reviews safely.");
        }

        var auth = await RunAsync(["auth", "status", "--json"], home, ct).ConfigureAwait(false);
        var loggedIn = TryParse(auth.StandardOutput, out var status)
            && status.TryGetProperty("loggedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new AgentHealth(true, loggedIn, versionText, loggedIn ? null : "Not logged in: run 'claude auth login'.");
    }

    public async Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct)
    {
        if (!_verified)
        {
            var health = await CheckAsync(ct).ConfigureAwait(false);
            _verified = health.IsReady
                ? true
                : throw new AgentUnavailableException(health.Problem ?? "Claude Code is not ready.");
        }

        var attempt = context.AttemptFolder;
        var schema = ForCli(await File.ReadAllTextAsync(Path.Combine(attempt, "schema.json"), ct).ConfigureAwait(false));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(context.Timeout);
        try
        {
            string[] args =
            [
                "-p", ReviewPrompt(attempt),
                "--restricted",
                "--tools", ReadOnlyTools,
                "--strict-mcp-config",
                "--add-dir", attempt,
                "--output-format", "json",
                "--json-schema", schema,
                "--no-session-persistence",
                .. ModelArgs(),
            ];
            var output = await RunLoggedAsync(args, context.WorktreePath, attempt, OutputFile, timeout.Token).ConfigureAwait(false);
            if (TryValidate(output, out var report, out var error))
            {
                return report;
            }

            // One repair round: the model fixes its own JSON from a file, with only Read available.
            await File.WriteAllTextAsync(Path.Combine(attempt, InvalidOutputFile), output, timeout.Token).ConfigureAwait(false);
            string[] repairArgs =
            [
                "-p", RepairPrompt(Path.Combine(attempt, InvalidOutputFile), error),
                "--restricted",
                "--tools", "Read",
                "--strict-mcp-config",
                "--output-format", "json",
                "--json-schema", schema,
                "--no-session-persistence",
                .. ModelArgs(),
            ];
            var repaired = await RunLoggedAsync(repairArgs, attempt, attempt, RepairOutputFile, timeout.Token).ConfigureAwait(false);
            return TryValidate(repaired, out report, out var repairError)
                ? report
                : throw new AgentOutputException($"Output still invalid after repair: {repairError}");
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AgentFailedException($"Claude Code timed out after {context.Timeout}.", e);
        }
    }

    /// <summary>Runs the CLI, records stdout and the log, and returns the JSON the model produced.</summary>
    private async Task<string> RunLoggedAsync(string[] args, string cwd, string attempt, string outputFile, CancellationToken ct)
    {
        var result = await RunAsync(args, cwd, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(attempt, outputFile), result.StandardOutput, ct).ConfigureAwait(false);
        await File.AppendAllTextAsync(
            Path.Combine(attempt, LogFile),
            $"$ {options.Executable} ({cwd}) exit {result.ExitCode}\n{result.StandardError}\n",
            ct).ConfigureAwait(false);

        if (!TryParse(result.StandardOutput, out var envelope))
        {
            throw Classify(null, $"{result.StandardError}\n{result.StandardOutput}".Trim(), result.ExitCode);
        }

        var isError = envelope.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
        var resultText = envelope.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
        if (isError || result.ExitCode != 0)
        {
            var apiStatus = envelope.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.Number
                && s.TryGetInt32(out var code) ? code : (int?)null;
            throw Classify(apiStatus, resultText, result.ExitCode);
        }

        return envelope.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object
            ? structured.GetRawText()
            : ExtractJson(resultText);
    }

    // The CLI's validator rejects a draft 2020-12 "$schema" declaration; the schema itself is draft-07 compatible.
    private static string ForCli(string schema)
    {
        var node = JsonNode.Parse(schema)!.AsObject();
        node.Remove("$schema");
        return node.ToJsonString();
    }

    private static bool TryValidate(string json, out FindingsReport report, out string error)
    {
        try
        {
            report = FindingsJson.Deserialize(json);
            error = "";
            return true;
        }
        catch (JsonException e)
        {
            report = null!;
            error = e.Message;
            return false;
        }
    }

    private static AgentException Classify(int? apiStatus, string message, int exitCode)
    {
        var detail = message.Length > 500 ? message[..500] : message;
        if (apiStatus == 429 || Throttled().IsMatch(message))
        {
            // Subscription limit messages end with "|<unix seconds>" of the reset time.
            var reset = ResetTime().Match(message);
            DateTimeOffset? retryAt = reset.Success
                ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(reset.Groups[1].Value, CultureInfo.InvariantCulture))
                : null;
            return new AgentThrottledException($"Claude Code is rate limited: {detail}", retryAt);
        }

        return apiStatus is 401 or 403 || AuthProblem().IsMatch(message)
            ? new AgentUnavailableException($"Claude Code is not authenticated: {detail}")
            : new AgentFailedException($"Claude Code failed (exit {exitCode}): {detail}");
    }

    private static string ExtractJson(string text)
    {
        var fenced = JsonFence().Match(text);
        if (fenced.Success)
        {
            return fenced.Groups[1].Value;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static bool TryParse(string json, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken ct) =>
        runner.RunAsync(
            new ProcessSpec(options.Executable, args, cwd,
                AgentEnvironment.Scrubbed(Environment.GetEnvironmentVariables(), new Dictionary<string, string?>())),
            ct);

    private string[] ModelArgs() => options.Model is { } model ? ["--model", model] : [];

    private static string ReviewPrompt(string attempt) => $"""
        Review the pull request checked out in the current directory.

        1. Read {Path.Combine(attempt, "instructions.md")} and follow it.
        2. Read {Path.Combine(attempt, "context.json")} for the pull request metadata.
        3. Read {Path.Combine(attempt, "delta.patch")}: it holds the changes under review.

        Report findings only for lines changed in delta.patch. Use file paths relative to the current directory and
        line numbers in the current files. Respond with the findings JSON object only.
        """;

    private static string RepairPrompt(string invalidFile, string error) => $"""
        Read {invalidFile}. It holds a code review result that does not match the required JSON schema.
        Validation error: {error}
        Return the same review as a JSON object that matches the schema. Keep the content; fix only the structure.
        """;

    [GeneratedRegex(@"usage limit|rate limit|too many requests|overloaded", RegexOptions.IgnoreCase)]
    private static partial Regex Throttled();

    [GeneratedRegex(@"\|(\d{10})\b")]
    private static partial Regex ResetTime();

    [GeneratedRegex(@"not logged in|/login|log in|invalid api key|authentication|oauth token", RegexOptions.IgnoreCase)]
    private static partial Regex AuthProblem();

    [GeneratedRegex(@"```(?:json)?\s*\n(\{[\s\S]*\})\s*\n```")]
    private static partial Regex JsonFence();
}
