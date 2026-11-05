using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;

namespace Reviewer.Agents.Cli;

/// <param name="Executable">The <c>codex</c> binary, resolved from PATH by default.</param>
/// <param name="Model">Model name; null uses the CLI's default for the subscription.</param>
public sealed record CodexOptions(string Executable = "codex", string? Model = null);

/// <summary>
/// Runs Codex headless (<c>codex exec</c>) in the PR worktree under the user's own subscription.
/// </summary>
/// <remarks>
/// Codex reads files through a shell, so it can run commands, the PR's own code included. Isolation therefore rests on
/// its sandbox, through a permission profile (verified on 0.162.1): commands may read only system files, the worktree
/// and the attempt folder, write nothing, and reach no network. The user's config, rules, plugins, apps, hooks and the
/// worktree's <c>AGENTS.md</c> aren't loaded, web search is off, and commands inherit only a core environment. A CLI
/// older than <see cref="MinimumVersion"/> or without the flags this relies on is refused rather than run unprotected.
/// </remarks>
public sealed partial class CodexAgent(CodexOptions options, IProcessRunner runner, TimeProvider? time = null) : IReviewAgent
{
    public const string OutputFile = "output.json";
    public const string LogFile = "agent.log";
    public const string SchemaFile = "codex-schema.json";

    /// <summary>The oldest CLI the permission profile below was verified on.</summary>
    public static readonly Version MinimumVersion = new(0, 162, 0);

    private const string EventsFile = "events.jsonl";
    private const string InvalidOutputFile = "invalid-output.json";
    private const string RepairOutputFile = "output-repair.json";
    private const string RepairEventsFile = "events-repair.jsonl";
    private const string Profile = "reviewer";

    private static readonly string[] RequiredFlags =
        ["--output-schema", "--output-last-message", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--json"];

    // Unknown feature names fail the run, so a renamed feature stops reviews instead of staying on.
    private static readonly string[] DisabledFeatures = ["apps", "plugins", "hooks", "browser_use", "computer_use", "memories"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Set once a health check passes, so later reviews skip the extra CLI calls.
    private bool _verified;

    public string Id => "codex";

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

        // "codex-cli 0.162.1"
        var versionText = version.StandardOutput.Trim().Split(' ').LastOrDefault();
        if (version.ExitCode != 0 || string.IsNullOrEmpty(versionText))
        {
            return new AgentHealth(false, false, null, $"'{options.Executable} --version' failed: {version.StandardError.Trim()}");
        }

        if (!Version.TryParse(versionText.Split('-')[0], out var parsed) || parsed < MinimumVersion)
        {
            return new AgentHealth(true, false, versionText,
                $"Codex {versionText} is older than {MinimumVersion}; update it to run reviews safely.");
        }

        var help = await RunAsync(["exec", "--help"], home, ct).ConfigureAwait(false);
        var missing = RequiredFlags.Where(f => !help.StandardOutput.Contains(f, StringComparison.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            return new AgentHealth(true, false, versionText,
                $"Codex {versionText} lacks {string.Join(", ", missing)}; update it to run reviews safely.");
        }

        var login = await RunAsync(["login", "status"], home, ct).ConfigureAwait(false);
        var loggedIn = login.ExitCode == 0;
        return new AgentHealth(true, loggedIn, versionText, loggedIn ? null : "Not logged in: run 'codex login'.");
    }

    public async Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct)
    {
        if (!_verified)
        {
            var health = await CheckAsync(ct).ConfigureAwait(false);
            _verified = health.IsReady
                ? true
                : throw new AgentUnavailableException(health.Problem ?? "Codex is not ready.");
        }

        var attempt = context.AttemptFolder;
        var schemaPath = Path.Combine(attempt, SchemaFile);
        var schema = await File.ReadAllTextAsync(Path.Combine(attempt, "schema.json"), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(schemaPath, ForCodex(schema), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(context.Timeout);
        try
        {
            string[] args =
            [
                "exec",
                .. Isolation([context.WorktreePath, attempt]),
                "--output-schema", schemaPath,
                "--output-last-message", Path.Combine(attempt, OutputFile),
                .. ModelArgs(),
                "--", AgentOutput.ReviewPrompt(attempt),
            ];
            var output = await RunLoggedAsync(args, context.WorktreePath, attempt, OutputFile, EventsFile, timeout.Token).ConfigureAwait(false);
            if (AgentOutput.TryValidate(output, out var report, out var error))
            {
                return report;
            }

            // One repair round in the attempt folder, which is all the model can read.
            await File.WriteAllTextAsync(Path.Combine(attempt, InvalidOutputFile), output, timeout.Token).ConfigureAwait(false);
            string[] repairArgs =
            [
                "exec",
                .. Isolation([attempt]),
                "--output-schema", schemaPath,
                "--output-last-message", Path.Combine(attempt, RepairOutputFile),
                .. ModelArgs(),
                "--", AgentOutput.RepairPrompt(Path.Combine(attempt, InvalidOutputFile), error),
            ];
            var repaired = await RunLoggedAsync(repairArgs, attempt, attempt, RepairOutputFile, RepairEventsFile, timeout.Token).ConfigureAwait(false);
            return AgentOutput.TryValidate(repaired, out report, out var repairError)
                ? report
                : throw new AgentOutputException($"Output still invalid after repair: {repairError}");
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AgentFailedException($"Codex timed out after {context.Timeout}.", e);
        }
    }

    /// <summary>The flags that confine a run: what its commands may read, and what of the user's setup it loads.</summary>
    internal static string[] Isolation(IReadOnlyList<string> readable)
    {
        var filesystem = string.Join(", ", readable.Select(path => $"{TomlLiteral(path)} = \"read\"").Prepend("\":minimal\" = \"read\""));
        return
        [
            "--ignore-user-config",
            "--ignore-rules",
            "--ephemeral",
            // Its trust check guards writes, which the sandbox already forbids; the repair runs outside any repo.
            "--skip-git-repo-check",
            "--json",
            "--color", "never",
            "-c", $"default_permissions=\"{Profile}\"",
            "-c", $"permissions.{Profile}.filesystem={{ {filesystem} }}",
            "-c", $"permissions.{Profile}.network.enabled=false",
            "-c", "project_doc_max_bytes=0",
            "-c", "web_search=\"disabled\"",
            "-c", "shell_environment_policy.inherit=\"core\"",
            .. DisabledFeatures.SelectMany(f => new[] { "--disable", f }),
        ];
    }

    /// <summary>Runs the CLI, records its events and the log, and returns the model's last message.</summary>
    private async Task<string> RunLoggedAsync(
        string[] args, string cwd, string attempt, string outputFile, string eventsFile, CancellationToken ct)
    {
        var lastMessage = Path.Combine(attempt, outputFile);
        File.Delete(lastMessage);
        var result = await RunAsync(args, cwd, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(attempt, eventsFile), result.StandardOutput, ct).ConfigureAwait(false);
        await File.AppendAllTextAsync(
            Path.Combine(attempt, LogFile),
            $"$ {options.Executable} ({cwd}) exit {result.ExitCode}\n{result.StandardError}\n",
            ct).ConfigureAwait(false);

        if (Failure(result.StandardOutput) is { } failure)
        {
            throw Classify(failure, result.ExitCode);
        }

        if (result.ExitCode != 0 || !File.Exists(lastMessage))
        {
            throw Classify($"{result.StandardError}\n{result.StandardOutput}".Trim(), result.ExitCode);
        }

        return AgentOutput.ExtractJson(await File.ReadAllTextAsync(lastMessage, ct).ConfigureAwait(false));
    }

    /// <summary>Why the turn failed, from the JSONL events: the <c>turn.failed</c> error, else the last error event.</summary>
    internal static string? Failure(string events)
    {
        string? failed = null, lastError = null;
        foreach (var line in events.Split('\n'))
        {
            if (!AgentOutput.TryParseObject(line, out var e) || !e.TryGetProperty("type", out var type))
            {
                continue;
            }

            switch (type.GetString())
            {
                case "turn.failed" when e.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object:
                    failed = error.TryGetProperty("message", out var m) ? m.GetString() : "turn failed";
                    break;
                case "error" when e.TryGetProperty("message", out var message):
                    lastError = message.GetString();
                    break;
            }
        }

        return failed ?? (lastError is not null && !lastError.StartsWith("Reconnecting", StringComparison.Ordinal) ? lastError : null);
    }

    // OpenAI's strict structured output needs every property listed as required; optional ones must allow null instead.
    // The findings schema's optional properties (suggestion, and the summary's risk, changes, checked, notVerified) already do.
    internal static string ForCodex(string schema)
    {
        var node = JsonNode.Parse(schema)!.AsObject();
        node.Remove("$schema");
        RequireAll(node);
        return node.ToJsonString();

        static void RequireAll(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["properties"] is JsonObject properties)
                {
                    obj["required"] = new JsonArray([.. properties.Select(p => JsonValue.Create(p.Key))]);
                }

                foreach (var (_, child) in obj.ToList())
                {
                    RequireAll(child);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    RequireAll(child);
                }
            }
        }
    }

    private AgentException Classify(string message, int exitCode)
    {
        var detail = AgentOutput.Truncate(message);
        if (Throttled().IsMatch(message))
        {
            return new AgentThrottledException($"Codex is rate limited: {detail}", RetryAt(message));
        }

        return AuthProblem().IsMatch(message)
            ? new AgentUnavailableException($"Codex is not authenticated: {detail}")
            : new AgentFailedException($"Codex failed (exit {exitCode}): {detail}");
    }

    // Usage limit messages say when to retry relative to now, e.g. "try again in 1 day 2 hours 5 minutes".
    private DateTimeOffset? RetryAt(string message)
    {
        var tail = TryAgainIn().Match(message);
        if (!tail.Success)
        {
            return null;
        }

        var wait = TimeSpan.Zero;
        foreach (Match part in Duration().Matches(tail.Groups[1].Value))
        {
            var amount = int.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            wait += part.Groups[2].Value.ToLowerInvariant() switch
            {
                "day" => TimeSpan.FromDays(amount),
                "hour" => TimeSpan.FromHours(amount),
                "minute" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
        }

        return wait > TimeSpan.Zero ? _time.GetUtcNow() + wait : null;
    }

    // TOML literal strings take paths as they are, Windows backslashes included; they can't hold a quote or newline.
    private static string TomlLiteral(string path) =>
        path.IndexOfAny(['\'', '\n', '\r']) < 0
            ? $"'{path}'"
            : throw new AgentFailedException($"Codex can't be confined to '{path}': the path holds a quote or line break.");

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken ct) =>
        runner.RunAsync(
            new ProcessSpec(options.Executable, args, cwd,
                AgentEnvironment.Scrubbed(Environment.GetEnvironmentVariables(), new Dictionary<string, string?>(), AgentEnvironment.CodexAuth)),
            ct);

    private string[] ModelArgs() => options.Model is { } model ? ["--model", model] : [];

    [GeneratedRegex(@"usage limit|rate limit|too many requests|\b429\b|overloaded", RegexOptions.IgnoreCase)]
    private static partial Regex Throttled();

    [GeneratedRegex(@"try again in ((?:\s*,?\s*(?:and\s+)?\d+\s*(?:day|hour|minute|second)s?)+)", RegexOptions.IgnoreCase)]
    private static partial Regex TryAgainIn();

    [GeneratedRegex(@"(\d+)\s*(day|hour|minute|second)", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    [GeneratedRegex(@"not logged in|codex login|\b401\b|\b403\b|unauthorized|invalid api key|authentication", RegexOptions.IgnoreCase)]
    private static partial Regex AuthProblem();
}
