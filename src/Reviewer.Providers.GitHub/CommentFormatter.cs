using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub;

/// <summary>
/// Renders findings as GitHub Markdown. Finding text comes from a model that read untrusted PR content, so it is
/// defused: no @-mentions that would ping people, and no HTML comments that could fake our fingerprint marker.
/// </summary>
internal static partial class CommentFormatter
{
    public const int MaxBodyLength = 65_000;

    public static string InlineBody(InlineComment comment, bool withSuggestion)
    {
        var f = comment.Finding;
        var body = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"**{Label(f.Severity)} · {Label(f.Category)}**: {Defuse(f.Title)}\n\n")
            .Append(Defuse(f.Body));

        if (withSuggestion && f.Suggestion is { } suggestion)
        {
            var fence = new string('`', Math.Max(3, LongestBacktickRun(suggestion) + 1));
            body.Append(CultureInfo.InvariantCulture, $"\n\n{fence}suggestion\n{suggestion.TrimEnd('\n')}\n{fence}");
        }

        return body.Append('\n').Append(Marker(comment.Fingerprint)).ToString();
    }

    /// <param name="listed">Findings shown in the body rather than inline.</param>
    public static string ReviewBody(string summary, IReadOnlyList<Finding> listed)
    {
        var body = new StringBuilder(Defuse(summary));
        if (listed.Count > 0)
        {
            body.Append("\n\n### Other findings\n");
            foreach (var f in listed)
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"\n- **{Label(f.Severity)} · {Label(f.Category)}** `{f.File}:{f.StartLine}`: {Defuse(f.Title)}");
            }
        }

        return Truncate(body.ToString());
    }

    public static string? ParseFingerprint(string body)
    {
        var match = MarkerPattern().Match(body);
        return match.Success ? match.Groups["fp"].Value : null;
    }

    public static string Marker(string fingerprint) =>
        FingerprintPattern().IsMatch(fingerprint)
            ? $"<!-- reviewer:fp={fingerprint} -->"
            : throw new ArgumentException($"Invalid fingerprint '{fingerprint}'.", nameof(fingerprint));

    private static string Defuse(string text) =>
        Mention().Replace(text.Replace("<!--", "&lt;!--", StringComparison.Ordinal), "@​");

    private static string Truncate(string body) =>
        body.Length <= MaxBodyLength ? body : body[..MaxBodyLength] + "\n\n_(truncated)_";

    private static string Label<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static int LongestBacktickRun(string text) =>
        BacktickRun().Matches(text).Select(m => m.Length).DefaultIfEmpty(0).Max();

    [GeneratedRegex(@"@(?=[A-Za-z0-9])")]
    private static partial Regex Mention();

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRun();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex FingerprintPattern();

    [GeneratedRegex(@"<!-- reviewer:fp=(?<fp>[A-Za-z0-9_-]{1,128}) -->\s*$")]
    private static partial Regex MarkerPattern();
}
