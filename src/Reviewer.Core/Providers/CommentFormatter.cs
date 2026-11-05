using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Core.Providers;

/// <summary>
/// Renders findings as Markdown for GitHub and GitLab alike. Finding text comes from a model that read untrusted PR
/// content, so it is defused: no @-mentions that would ping people, and no HTML comments that could fake our
/// fingerprint marker, which both platforms hide.
/// </summary>
public static partial class CommentFormatter
{
    public const int MaxBodyLength = 65_000;

    /// <param name="suggestionInfo">
    /// The fence's info string for the platform's suggestion block, e.g. <c>suggestion</c> on GitHub or
    /// <c>suggestion:-2+0</c> on GitLab; null leaves the suggestion out.
    /// </param>
    public static string InlineBody(InlineComment comment, string? suggestionInfo)
    {
        var f = comment.Finding;
        var body = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"**{Label(f.Severity)} · {Label(f.Category)}**: {Defuse(f.Title)}\n\n")
            .Append(Defuse(f.Body));

        if (suggestionInfo is not null && f.Suggestion is { } suggestion)
        {
            var fence = new string('`', Math.Max(3, LongestBacktickRun(suggestion) + 1));
            body.Append(CultureInfo.InvariantCulture, $"\n\n{fence}{suggestionInfo}\n{suggestion.TrimEnd('\n')}\n{fence}");
        }

        return body.Append('\n').Append(Marker(comment.Fingerprint)).ToString();
    }

    /// <param name="summary">The top of the body, e.g. from <see cref="Headline"/>; defused here.</param>
    /// <param name="listed">Findings shown in the body rather than inline.</param>
    /// <param name="details">Engine-built text after the findings, e.g. from <see cref="Details"/>: already defused.</param>
    public static string ReviewBody(string summary, IReadOnlyList<Finding> listed, string details = "")
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

        if (details.Length > 0)
        {
            body.Append("\n\n").Append(details);
        }

        return Truncate(body.ToString());
    }

    /// <summary>
    /// The review's status line, then the agent's summary and why it rated the risk as it did. Model text is defused by
    /// <see cref="ReviewBody"/>, not here.
    /// </summary>
    /// <param name="posted">The findings this review posts, inline or in the body.</param>
    /// <param name="reReview">Only commits since the last review were reviewed.</param>
    public static string Headline(FindingsReport report, IReadOnlyList<Finding> posted, bool reReview)
    {
        var fresh = reReview ? "new " : "";
        var status = posted.Count == 0
            ? $"✅ No {fresh}issues found"
            : $"⚠️ {posted.Count} {fresh}finding{(posted.Count == 1 ? "" : "s")}: " + string.Join(", ",
                posted.GroupBy(f => f.Severity).OrderByDescending(g => g.Key).Select(g => $"{g.Count()} {Label(g.Key)}"));
        var body = new StringBuilder("## ").Append(status);
        if (report.Risk is { } risk)
        {
            body.Append(CultureInfo.InvariantCulture, $" · Risk: {Label(risk.Level)}");
        }

        if (report.Summary.Trim() is { Length: > 0 } summary)
        {
            body.Append("\n\n").Append(summary);
        }

        if (report.Risk is { Reason: var reason } && reason.Trim() is { Length: > 0 } why)
        {
            body.Append("\n\n**Risk:** ").Append(why);
        }

        return body.ToString();
    }

    /// <summary>
    /// The agent's walkthrough as collapsed sections: what changed, what it checked, and what it couldn't verify. The
    /// last one names its first item in the toggle, so a gap shows without opening it. Empty sections are left out.
    /// </summary>
    public static string Details(FindingsReport report, bool reReview)
    {
        var sections = new List<string>();
        if (report.Changes is { Count: > 0 } changes)
        {
            var rows = changes.Select(c => $"| {Cell(c.Area)} | {Cell(c.Description)} |");
            sections.Add(Collapsed(
                reReview ? "Changes since the last review" : "Changes",
                $" · {changes.Count} area{(changes.Count == 1 ? "" : "s")}",
                "| Area | What changed |\n|---|---|\n" + string.Join("\n", rows)));
        }

        if (Items(report.Checked) is { Count: > 0 } done)
        {
            sections.Add(Collapsed("What I checked", $" ({done.Count})", Bullets(done)));
        }

        if (Items(report.NotVerified) is { Count: > 0 } open)
        {
            sections.Add(Collapsed("Not verified", $" ({open.Count}): {Escape(Shorten(open[0], 100))}", Bullets(open)));
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>A short note from the engine, e.g. why a thread was resolved. No marker: it's not a finding.</summary>
    public static string Note(string text) => Truncate(Defuse(text));

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

    // GitHub and GitLab need the blank lines around the content to render its Markdown.
    private static string Collapsed(string title, string note, string content) =>
        $"<details>\n<summary><b>{title}</b>{note}</summary>\n\n{content}\n\n</details>";

    private static List<string> Items(IReadOnlyList<string>? items) =>
        [.. (items ?? []).Select(OneLine).Where(i => i.Length > 0)];

    private static string Bullets(IReadOnlyList<string> items) => string.Join("\n", items.Select(i => $"- {Inline(i)}"));

    private static string Cell(string text) => Inline(OneLine(text)).Replace("|", "\\|", StringComparison.Ordinal);

    // Model text inside a section: defused, and unable to close or open the collapsed sections around it.
    private static string Inline(string text) => DetailsTag().Replace(Defuse(text), "&lt;");

    // A <summary> toggle shows text, not Markdown, so the label is HTML-escaped instead.
    private static string Escape(string text) => Defuse(text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal));

    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string Truncate(string body) =>
        body.Length <= MaxBodyLength ? body : body[..MaxBodyLength] + "\n\n_(truncated)_";

    private static string Label<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static int LongestBacktickRun(string text) =>
        BacktickRun().Matches(text).Select(m => m.Length).DefaultIfEmpty(0).Max();

    [GeneratedRegex(@"@(?=[A-Za-z0-9])")]
    private static partial Regex Mention();

    [GeneratedRegex(@"<(?=/?(?:details|summary)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRun();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex FingerprintPattern();

    [GeneratedRegex(@"<!-- reviewer:fp=(?<fp>[A-Za-z0-9_-]{1,128}) -->\s*$")]
    private static partial Regex MarkerPattern();
}
