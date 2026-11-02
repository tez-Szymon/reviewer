using System.Globalization;
using System.Text.RegularExpressions;

namespace Reviewer.Providers.GitLab;

/// <summary>
/// One file's diff as GitLab returns it: hunks only, no file headers. GitLab anchors a comment on an added line by its
/// new line number, and on an unchanged line by both numbers, so a comment can only go on a line the diff shows.
/// </summary>
internal sealed partial class DiffLines
{
    private readonly Dictionary<int, int?> _oldLineByNewLine = [];

    public int Additions { get; private set; }

    public int Deletions { get; private set; }

    public static DiffLines Parse(string? diff)
    {
        var result = new DiffLines();
        int oldLine = 0, newLine = 0;
        var inHunk = false;
        foreach (var line in (diff ?? "").Split('\n'))
        {
            if (HunkHeader().Match(line) is { Success: true } hunk)
            {
                oldLine = int.Parse(hunk.Groups["old"].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(hunk.Groups["new"].Value, CultureInfo.InvariantCulture);
                inHunk = true;
                continue;
            }

            if (!inHunk)
            {
                continue;
            }

            switch (line.FirstOrDefault())
            {
                case '+':
                    result._oldLineByNewLine[newLine++] = null;
                    result.Additions++;
                    break;
                case '-':
                    oldLine++;
                    result.Deletions++;
                    break;
                case ' ':
                    result._oldLineByNewLine[newLine++] = oldLine++;
                    break;
            }
        }

        return result;
    }

    /// <summary>Whether a comment can go on <paramref name="newLine"/>, and the old line it needs if the line is unchanged.</summary>
    public bool TryGetAnchor(int newLine, out int? oldLine) => _oldLineByNewLine.TryGetValue(newLine, out oldLine);

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}
