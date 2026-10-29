using System.Globalization;
using System.Text.RegularExpressions;

namespace Reviewer.Git;

internal static partial class DiffParser
{
    /// <summary>
    /// Paths in the newer revision from <c>git diff --name-status -z</c>. Deleted files are skipped;
    /// renames and copies yield their new path.
    /// </summary>
    public static IEnumerable<string> ParseNameStatus(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i][0];
            if (status is 'R' or 'C')
            {
                i += 2;
                yield return fields[i];
            }
            else
            {
                i++;
                if (status != 'D')
                {
                    yield return fields[i];
                }
            }
        }
    }

    /// <summary>Changed line spans per new path, from <c>git diff --unified=0</c> output.</summary>
    public static Dictionary<string, IReadOnlyList<LineSpan>> ParseChangedLines(string patch)
    {
        var result = new Dictionary<string, IReadOnlyList<LineSpan>>(StringComparer.Ordinal);
        List<LineSpan>? current = null;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = NewPath(line[4..]);
                current = path is null ? null : [];
                if (path is not null)
                {
                    result[path] = current!;
                }
            }
            else if (current is not null && line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = HunkHeader().Match(line);
                if (!match.Success)
                {
                    throw new GitException($"Unexpected hunk header: {line}");
                }

                var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
                var count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 1;

                // A pure deletion reports the line before it; anchor it to the line after instead.
                current.Add(count == 0 ? new LineSpan(start + 1, start + 1) : new LineSpan(start, start + count - 1));
            }
        }

        return result;
    }

    private static string? NewPath(string field)
    {
        field = field.TrimEnd('\t', '\r');
        if (field == "/dev/null")
        {
            return null;
        }

        if (field.StartsWith('"'))
        {
            field = Unquote(field);
        }

        return field.StartsWith("b/", StringComparison.Ordinal) ? field[2..] : field;
    }

    // Git C-quotes paths with control characters, quotes or backslashes even with core.quotePath=false.
    private static string Unquote(string quoted)
    {
        var bytes = new List<byte>();
        for (var i = 1; i < quoted.Length - 1; i++)
        {
            var c = quoted[i];
            if (c != '\\')
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            c = quoted[++i];
            if (c is >= '0' and <= '7')
            {
                bytes.Add(Convert.ToByte(quoted.Substring(i, 3), 8));
                i += 2;
                continue;
            }

            bytes.Add(c switch
            {
                'a' => (byte)'\a',
                'b' => (byte)'\b',
                'f' => (byte)'\f',
                'n' => (byte)'\n',
                'r' => (byte)'\r',
                't' => (byte)'\t',
                'v' => (byte)'\v',
                _ => (byte)c,
            });
        }

        return System.Text.Encoding.UTF8.GetString([.. bytes]);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
    private static partial Regex HunkHeader();
}
