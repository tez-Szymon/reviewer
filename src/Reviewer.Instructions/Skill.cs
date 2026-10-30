namespace Reviewer.Instructions;

/// <summary>A Markdown skill: extra review guidance from the user (layer 3) or the repo's config (layer 4).</summary>
/// <param name="Name">From the frontmatter, or the file name without extension.</param>
/// <param name="AppliesTo">Globs over changed file paths; the skill is used only when one matches. Empty applies always.</param>
/// <param name="Categories">Finding categories the skill is about, shown to the agent as its focus.</param>
/// <param name="Source">Where the skill came from, for the agent log and history.</param>
public sealed record Skill(string Name, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Categories, string Body, string Source)
{
    /// <summary>
    /// Parses optional frontmatter with <c>name</c>, <c>appliesTo</c> and <c>categories</c>. Values are scalars or
    /// flow lists (<c>[a, "b"]</c>); unknown keys are ignored.
    /// </summary>
    public static Skill Parse(string source, string text)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        IReadOnlyList<string> appliesTo = [];
        IReadOnlyList<string> categories = [];

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var body = text;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end > 0)
            {
                foreach (var line in lines[1..end])
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var value = line[(colon + 1)..].Trim();
                    switch (line[..colon].Trim())
                    {
                        case "name" when Unquote(value).Length > 0:
                            name = Unquote(value);
                            break;
                        case "appliesTo":
                            appliesTo = ParseList(value);
                            break;
                        case "categories":
                            categories = ParseList(value);
                            break;
                    }
                }

                body = string.Join('\n', lines[(end + 1)..]);
            }
        }

        return new Skill(name, appliesTo, categories, body.Trim(), source);
    }

    private static string[] ParseList(string value)
    {
        var items = value.StartsWith('[') && value.EndsWith(']') ? value[1..^1].Split(',') : [value];
        return [.. items.Select(Unquote).Where(i => i.Length > 0)];
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
    }
}
