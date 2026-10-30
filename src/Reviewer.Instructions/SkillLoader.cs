using Microsoft.Extensions.FileSystemGlobbing;

namespace Reviewer.Instructions;

/// <summary>Loads skills from files on the user's machine, e.g. <c>~/.reviewer/skills/*.md</c>.</summary>
public static class SkillLoader
{
    private static readonly char[] Wildcards = ['*', '?', '['];

    /// <summary>Skills matching any of <paramref name="patterns"/>, in path order. A missing directory yields none.</summary>
    /// <param name="home">Replaces a leading <c>~</c>.</param>
    public static async Task<IReadOnlyList<Skill>> LoadAsync(IEnumerable<string> patterns, string home, CancellationToken ct)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            var (root, glob) = Split(ExpandHome(pattern, home));
            if (glob is null)
            {
                if (File.Exists(root))
                {
                    paths.Add(Path.GetFullPath(root));
                }
            }
            else if (Directory.Exists(root))
            {
                var matcher = new Matcher(StringComparison.Ordinal);
                matcher.AddInclude(glob);
                paths.UnionWith(matcher.GetResultsInFullPath(root));
            }
        }

        var skills = new List<Skill>(paths.Count);
        foreach (var path in paths)
        {
            skills.Add(Skill.Parse(path, await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
        }

        return skills;
    }

    private static string ExpandHome(string pattern, string home) =>
        pattern == "~" || pattern.StartsWith("~/", StringComparison.Ordinal) ? home + pattern[1..] : pattern;

    // Splits "/a/b/**/*.md" into the directory before the first wildcard and the glob relative to it.
    private static (string Root, string? Glob) Split(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var wildcard = normalized.IndexOfAny(Wildcards);
        if (wildcard < 0)
        {
            return (pattern, null);
        }

        var slash = normalized.LastIndexOf('/', wildcard);
        if (slash < 0)
        {
            return (".", normalized);
        }

        // Keep the separator for filesystem roots ("/", "C:/").
        var root = normalized[..slash];
        return (root.Length == 0 || root.EndsWith(':') ? root + "/" : root, normalized[(slash + 1)..]);
    }
}
