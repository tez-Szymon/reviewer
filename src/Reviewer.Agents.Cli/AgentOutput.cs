using System.Text.Json;
using System.Text.RegularExpressions;
using Reviewer.Core.Findings;

namespace Reviewer.Agents.Cli;

/// <summary>Reading what a CLI agent printed: JSON objects, and the findings report inside its answer.</summary>
internal static partial class AgentOutput
{
    public static bool TryValidate(string json, out FindingsReport report, out string error)
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

    /// <summary>The JSON object in a free-text answer: a fenced block, or the outermost braces.</summary>
    public static string ExtractJson(string text)
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

    public static bool TryParseObject(string json, out JsonElement element)
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

    public static string Truncate(string message) => message.Length > 500 ? message[..500] : message;

    public static string ReviewPrompt(string attempt) => $"""
        Review the pull request checked out in the current directory.

        1. Read {Path.Combine(attempt, "instructions.md")} and follow it.
        2. Read {Path.Combine(attempt, "context.json")} for the pull request metadata.
        3. Read {Path.Combine(attempt, "delta.patch")}: it holds the changes under review.

        Report findings only for lines changed in delta.patch. Use file paths relative to the current directory and
        line numbers in the current files. Respond with the findings JSON object only.
        """;

    public static string RepairPrompt(string invalidFile, string error) => $"""
        Read {invalidFile}. It holds a code review result that does not match the required JSON schema.
        Validation error: {error}
        Return the same review as a JSON object that matches the schema. Keep the content; fix only the structure.
        """;

    [GeneratedRegex(@"```(?:json)?\s*\n(\{[\s\S]*\})\s*\n```")]
    private static partial Regex JsonFence();
}
