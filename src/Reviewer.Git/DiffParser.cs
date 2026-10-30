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
        var inHunks = false; // Past a file's first @@, "+++ x" is an added line "++ x", not a header.
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunks = false;
            }
            else if (!inHunks && line.StartsWith("+++ ", StringComparison.Ordinal))
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
                inHunks = true;
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

    /// <summary>Changed line spans per old path, from <c>git diff --unified=0</c> output.</summary>
    public static Dictionary<string, IReadOnlyList<LineSpan>> ParseOldChangedLines(string patch)
    {
        var result = new Dictionary<string, IReadOnlyList<LineSpan>>(StringComparer.Ordinal);
        List<LineSpan>? current = null;
        string? oldPath = null;
        var inHunks = false;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                oldPath = null;
                current = null;
                inHunks = false;
            }
            else if (inHunks)
            {
                if (current is not null && line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    current.Add(OldSpan(line));
                }
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = Path(line[4..], "a/");
                current = oldPath is null ? null : [];
                if (oldPath is not null)
                {
                    result[oldPath] = current!;
                }
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal) && oldPath is not null && Path(line[4..], "b/") is null)
            {
                current!.Clear();
                current.Add(new LineSpan(1, int.MaxValue)); // Deleted: everything written against it is gone.
                current = null;
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunks = true;
                current?.Add(OldSpan(line));
            }
        }

        return result;
    }

    private static LineSpan OldSpan(string header)
    {
        var match = OldHunkHeader().Match(header);
        if (!match.Success)
        {
            throw new GitException($"Unexpected hunk header: {header}");
        }

        var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
        var count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 1;

        // A pure insertion after line n becomes the empty gap (n+1, n).
        return count == 0 ? new LineSpan(start + 1, start) : new LineSpan(start, start + count - 1);
    }

    /// <summary>
    /// A hash per file of what a patch changes in it, ignoring line numbers and whitespace, so the same change to a
    /// file hashes the same after a rebase. Keyed by new path, or old path for deletions. Full <c>git diff</c> input.
    /// </summary>
    public static Dictionary<string, string> FilePatchIds(string patch)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in Sections(patch))
        {
            string? oldPath = null, newPath = null;
            var binary = section.Any(l => l.StartsWith("Binary files", StringComparison.Ordinal));
            var inHunks = false;
            var content = new System.Text.StringBuilder();
            foreach (var line in section.Skip(1))
            {
                if (inHunks || line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    // Hunk headers carry line numbers, which a rebase shifts; only the changed lines count.
                    inHunks = true;
                    if (line.Length > 0 && line[0] is '+' or '-')
                    {
                        content.Append(line[0]).Append(string.Join(' ', line[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Append('\n');
                    }
                }
                else if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    oldPath = Path(line[4..], "a/");
                }
                else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    newPath = Path(line[4..], "b/");
                }
                else if (!line.StartsWith("index ", StringComparison.Ordinal) || binary)
                {
                    // Mode, rename and binary lines; text files' blob ids change with the base, so they're left out.
                    content.Append(line).Append('\n');
                }
            }

            // A rename without content changes has no ---/+++ lines; its paths are in the header.
            var key = newPath ?? oldPath ?? RenameTarget(section);
            if (key is not null)
            {
                var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content.ToString()));
                result[key] = Convert.ToHexStringLower(hash.AsSpan(0, 12));
            }
        }

        return result;
    }

    private static IEnumerable<List<string>> Sections(string patch)
    {
        List<string>? section = null;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (section is not null)
                {
                    yield return section;
                }

                section = [];
            }

            section?.Add(line);
        }

        if (section is not null)
        {
            yield return section;
        }
    }

    private static string? RenameTarget(List<string> section) =>
        section.FirstOrDefault(l => l.StartsWith("rename to ", StringComparison.Ordinal)) is { } to
            ? Unquoted(to["rename to ".Length..])
            : null;

    private static string Unquoted(string field) => field.StartsWith('"') ? Unquote(field) : field;

    private static string? NewPath(string field) => Path(field, "b/");

    private static string? Path(string field, string prefix)
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

        return field.StartsWith(prefix, StringComparison.Ordinal) ? field[prefix.Length..] : field;
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

    [GeneratedRegex(@"^@@ -(?<start>\d+)(?:,(?<count>\d+))? \+\d+(?:,\d+)? @@")]
    private static partial Regex OldHunkHeader();
}
