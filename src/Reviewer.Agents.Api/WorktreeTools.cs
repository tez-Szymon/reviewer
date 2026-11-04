using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Reviewer.Git;

namespace Reviewer.Agents.Api;

/// <summary>
/// The only tools an API agent gets: reading the PR's worktree, and files at a commit from the repo. Nothing writes,
/// runs code or reaches the network, so a PR can't use the agent to act. Every path stays inside the worktree: absolute
/// paths, <c>..</c>, symbolic links and <c>.git</c> are refused, and every answer is capped in size.
/// </summary>
/// <param name="git">The repo the worktree came from, for <c>git_show</c>; without it that tool isn't offered.</param>
internal sealed partial class WorktreeTools(string worktree, IGitRepository? git)
{
    /// <summary>The most text one tool answer carries; the rest is cut, with a note saying so.</summary>
    public const int MaxAnswerChars = 40_000;

    public const int MaxReadLines = 1_000;

    public const int MaxGrepMatches = 100;

    private const long MaxFileBytes = 2_000_000;
    private const int MaxListEntries = 500;
    private const int MaxGrepFiles = 5_000;
    private const int MaxLineChars = 300;

    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));

    public IReadOnlyList<AITool> All =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file", "Read a text file in the pull request's checkout, with line numbers."),
        AIFunctionFactory.Create(ListDir, "list_dir", "List a directory in the pull request's checkout; directories end with '/'."),
        AIFunctionFactory.Create(Grep, "grep", "Search the checkout's text files for a regular expression; answers path:line: text."),
        .. git is null
            ? Array.Empty<AITool>()
            : [AIFunctionFactory.Create(GitShowAsync, "git_show", "Read a file as it was at a commit, such as the base commit in the context.")],
    ];

    public string ReadFile(
        [Description("Path relative to the checkout's root.")] string path,
        [Description("First line to read, from 1.")] int startLine = 1,
        [Description("How many lines to read, at most 1000.")] int lineCount = 400)
    {
        if (Resolve(path, out var full) is { } problem)
        {
            return problem;
        }

        var file = new FileInfo(full);
        if (!file.Exists)
        {
            return Directory.Exists(full) ? $"{path} is a directory; use list_dir." : $"{path} doesn't exist.";
        }

        if (file.Length > MaxFileBytes)
        {
            return $"{path} is {file.Length} bytes, too large to read; use grep to find what you need in it.";
        }

        if (IsBinary(full))
        {
            return $"{path} is a binary file.";
        }

        var lines = File.ReadAllLines(full);
        var first = Math.Max(1, startLine);
        var count = Math.Clamp(lineCount, 1, MaxReadLines);
        if (first > lines.Length)
        {
            return $"{path} has {lines.Length} lines; there's no line {first}.";
        }

        var last = Math.Min(lines.Length, first + count - 1);
        var answer = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{path}, lines {first}-{last} of {lines.Length}:\n");
        for (var i = first; i <= last; i++)
        {
            answer.Append(CultureInfo.InvariantCulture, $"{i,6}\t{lines[i - 1]}\n");
        }

        return Cap(answer.ToString());
    }

    public string ListDir([Description("Directory relative to the checkout's root; '.' is the root.")] string path = ".")
    {
        if (Resolve(path, out var full) is { } problem)
        {
            return problem;
        }

        if (!Directory.Exists(full))
        {
            return File.Exists(full) ? $"{path} is a file; use read_file." : $"{path} doesn't exist.";
        }

        var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
            .Where(e => e.Name != ".git")
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.LinkTarget is not null ? $"{e.Name}@ (symbolic link, not followed)" : e is DirectoryInfo ? $"{e.Name}/" : e.Name)
            .Take(MaxListEntries + 1)
            .ToList();
        if (entries.Count == 0)
        {
            return $"{path} is empty.";
        }

        var more = entries.Count > MaxListEntries ? $"\n(more than {MaxListEntries} entries; only the first are listed)" : "";
        return Cap(string.Join('\n', entries.Take(MaxListEntries)) + more);
    }

    public string Grep(
        [Description("A .NET regular expression.")] string pattern,
        [Description("Directory or file to search, relative to the checkout's root.")] string path = ".")
    {
        if (Resolve(path, out var full) is { } problem)
        {
            return problem;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException e)
        {
            return $"Not a valid regular expression: {e.Message}";
        }

        IEnumerable<string> files = File.Exists(full)
            ? [full]
            : Directory.Exists(full)
                ? Directory.EnumerateFiles(full, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, // Symbolic links aren't followed.
                })
                : [];
        var matches = new List<string>();
        var searched = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
            if (relative.Split('/').Contains(".git") || new FileInfo(file).Length > MaxFileBytes || IsBinary(file))
            {
                continue;
            }

            if (++searched > MaxGrepFiles)
            {
                matches.Add($"(stopped after {MaxGrepFiles} files; search a narrower path)");
                break;
            }

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                try
                {
                    if (regex.IsMatch(line))
                    {
                        matches.Add($"{relative}:{number}: {(line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line)}");
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return "The regular expression took too long; use a simpler one.";
                }

                if (matches.Count >= MaxGrepMatches)
                {
                    matches.Add($"(stopped at {MaxGrepMatches} matches; narrow the pattern or the path)");
                    return Cap(string.Join('\n', matches));
                }
            }
        }

        return matches.Count == 0 ? "No matches." : Cap(string.Join('\n', matches));
    }

    public async Task<string> GitShowAsync(
        [Description("A full or abbreviated commit SHA, such as the base or head commit in the context.")] string revision,
        [Description("Path relative to the repository's root.")] string path,
        CancellationToken ct = default)
    {
        if (!Sha().IsMatch(revision))
        {
            return "revision must be a commit SHA of 7 to 40 hex digits.";
        }

        if (Relative(path) is not { } relative)
        {
            return $"'{path}' isn't a path inside the repository.";
        }

        try
        {
            var text = await git!.ReadFileAsync(revision, relative, ct).ConfigureAwait(false);
            return text is null ? $"{relative} doesn't exist at {revision}." : Cap(Numbered(text));
        }
        catch (GitException e)
        {
            return $"Couldn't read {relative} at {revision}: {e.Message}";
        }
    }

    /// <summary>The full path for <paramref name="path"/> inside the worktree, or why it isn't allowed.</summary>
    internal string? Resolve(string path, out string full)
    {
        full = "";
        if (Relative(path) is not { } relative)
        {
            return $"'{path}' isn't a path inside the checkout; use a path relative to its root.";
        }

        full = relative.Length == 0 ? _root : Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        // No link on the way may lead out of the worktree, so none is followed: a PR can add one to ~/.ssh.
        var current = _root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                return $"{path} goes through a symbolic link, which isn't followed.";
            }
        }

        return null;
    }

    /// <summary>A normalized path relative to the root, "" for the root itself, or null when it would leave it.</summary>
    private static string? Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(path) || path.StartsWith('~'))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                case ".git":
                    return null;
                default:
                    parts.Add(segment);
                    break;
            }
        }

        // git would read "-x" or ":x" as an option or a pathspec.
        var relative = string.Join('/', parts);
        return relative.StartsWith('-') || relative.StartsWith(':') ? null : relative;
    }

    private static bool IsBinary(string path)
    {
        Span<byte> head = stackalloc byte[8_000];
        using var stream = File.OpenRead(path);
        var read = stream.Read(head);
        return head[..read].Contains((byte)0);
    }

    private static string Numbered(string text)
    {
        var lines = text.Split('\n');
        var answer = new StringBuilder();
        for (var i = 0; i < lines.Length && i < MaxReadLines; i++)
        {
            answer.Append(CultureInfo.InvariantCulture, $"{i + 1,6}\t{lines[i]}\n");
        }

        return lines.Length > MaxReadLines ? answer.Append(CultureInfo.InvariantCulture, $"(first {MaxReadLines} of {lines.Length} lines)\n").ToString() : answer.ToString();
    }

    private static string Cap(string answer) =>
        answer.Length <= MaxAnswerChars ? answer : answer[..MaxAnswerChars] + $"\n(cut at {MaxAnswerChars} characters; ask for less)";

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex Sha();
}
