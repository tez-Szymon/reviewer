using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reviewer.Engine.Config;

namespace Reviewer.Engine.Eval;

/// <summary>A bug a review of the case should report: where it is in the head, and what it is.</summary>
/// <param name="File">Path in the head.</param>
/// <param name="StartLine">First line of the bug in the head, 1-based.</param>
/// <param name="About">What the bug is, for whoever reads a miss.</param>
public sealed record KnownBug(string File, int StartLine, int EndLine, string About)
{
    /// <summary>Identifies the bug across runs, e.g. <c>src/Pager.cs:12-14</c>.</summary>
    public string Key => StartLine == EndLine
        ? string.Create(CultureInfo.InvariantCulture, $"{File}:{StartLine}")
        : string.Create(CultureInfo.InvariantCulture, $"{File}:{StartLine}-{EndLine}");
}

/// <summary>The change a case reviews.</summary>
public abstract record EvalSource;

/// <summary>Two folders of files, committed by the eval: <c>before/</c> is the base, <c>after/</c> the head.</summary>
/// <param name="Before">Null when the change only adds files.</param>
public sealed record FixtureSource(string? Before, string After) : EvalSource;

/// <summary>Two commits in a git repository, fetched by SHA without credentials.</summary>
public sealed record RemoteSource(string Url, string BaseSha, string HeadSha) : EvalSource;

/// <summary>
/// One eval case: a change with known bugs, in its own folder with a <c>case.yaml</c>. A case with no bugs measures
/// noise: every finding on it is one too many.
/// </summary>
/// <param name="Id">The folder's name.</param>
/// <param name="Title">Shown to the agent as the PR title.</param>
/// <param name="Description">Shown to the agent as the PR description.</param>
public sealed record EvalCase(string Id, string Title, string Description, EvalSource Source, IReadOnlyList<KnownBug> Bugs)
{
    public const string FileName = "case.yaml";
}

/// <summary>Reads eval cases: each folder under the cases folder that has a <c>case.yaml</c>.</summary>
/// <remarks>
/// <code>
/// title: Paginate the order export
/// description: Optional PR description.
/// # A remote case names two commits; without these, before/ and after/ beside case.yaml are the change.
/// repo: https://github.com/owner/name.git
/// base: 40-hex SHA
/// head: 40-hex SHA
/// bugs:
///   - file: src/Export.cs
///     lines: 41-44        # or a single line
///     about: The last page is skipped when the count is a multiple of the page size.
/// </code>
/// </remarks>
public static partial class EvalCaseLoader
{
    public const string BeforeFolder = "before";
    public const string AfterFolder = "after";

    /// <summary>The cases under <paramref name="folder"/>, ordered by id.</summary>
    /// <exception cref="ConfigException">The folder is missing, or a case is invalid; every problem is listed.</exception>
    public static async Task<IReadOnlyList<EvalCase>> LoadAsync(string folder, CancellationToken ct)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder))
        {
            throw new ConfigException([$"No eval cases folder at {folder}."]);
        }

        var cases = new List<EvalCase>();
        var errors = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, EvalCase.FileName);
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                cases.Add(Parse(Path.GetFileName(dir), dir, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)));
            }
            catch (ConfigException e)
            {
                errors.AddRange(e.Errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigException(errors);
        }

        return cases.Count > 0 ? cases : throw new ConfigException([$"No eval cases in {folder}: each case is a folder with a {EvalCase.FileName}."]);
    }

    /// <param name="folder">The case's folder, which holds <c>before/</c> and <c>after/</c> for a fixture.</param>
    /// <exception cref="ConfigException">The case is invalid; every problem is listed.</exception>
    public static EvalCase Parse(string id, string folder, string yaml)
    {
        var source = Path.Combine(folder, EvalCase.FileName);
        var root = YamlToJson.Convert(yaml, source).Root;
        var errors = new List<string>();
        void Error(string message) => errors.Add($"{source}: {message}");

        string? Text(string key)
        {
            var node = root[key];
            if (node is null)
            {
                return null;
            }

            if (node.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } text)
            {
                return text;
            }

            Error($"'{key}' must be text.");
            return null;
        }

        foreach (var key in root.Select(p => p.Key).Where(k => k is not ("title" or "description" or "repo" or "base" or "head" or "bugs")))
        {
            Error($"unknown key '{key}'.");
        }

        var title = Text("title");
        if (title is null && root["title"] is null)
        {
            Error("'title' is required.");
        }

        var description = Text("description") ?? "";
        var (repo, baseSha, headSha) = (Text("repo"), Text("base"), Text("head"));
        EvalSource? evalSource = null;
        if (repo is not null || baseSha is not null || headSha is not null)
        {
            if (repo is null || baseSha is null || headSha is null)
            {
                Error("a remote case needs all of 'repo', 'base' and 'head'.");
            }
            else if (!Sha().IsMatch(baseSha) || !Sha().IsMatch(headSha))
            {
                Error("'base' and 'head' must be full 40-character commit SHAs, so the case never changes.");
            }
            else
            {
                evalSource = new RemoteSource(repo, baseSha.ToLowerInvariant(), headSha.ToLowerInvariant());
            }
        }
        else
        {
            var before = Path.Combine(folder, BeforeFolder);
            var after = Path.Combine(folder, AfterFolder);
            if (Directory.Exists(after))
            {
                evalSource = new FixtureSource(Directory.Exists(before) ? before : null, after);
            }
            else
            {
                Error($"a case needs either 'repo', 'base' and 'head', or an {AfterFolder}/ folder (and usually {BeforeFolder}/) beside {EvalCase.FileName}.");
            }
        }

        var bugs = new List<KnownBug>();
        switch (root["bugs"])
        {
            case null:
                break;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                {
                    if (Bug(items[i], $"bugs[{i}]", Error) is { } bug)
                    {
                        bugs.Add(bug);
                    }
                }

                break;
            default:
                Error("'bugs' must be a list.");
                break;
        }

        return errors.Count == 0
            ? new EvalCase(id, title!, description, evalSource!, bugs)
            : throw new ConfigException(errors);
    }

    private static KnownBug? Bug(JsonNode? node, string path, Action<string> error)
    {
        if (node is not JsonObject bug)
        {
            error($"'{path}' must have 'file', 'lines' and 'about'.");
            return null;
        }

        var file = bug["file"] is JsonValue f && f.GetValueKind() == JsonValueKind.String ? f.GetValue<string>() : null;
        var about = bug["about"] is JsonValue a && a.GetValueKind() == JsonValueKind.String ? a.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(about))
        {
            error($"'{path}' needs 'file' and 'about'.");
            return null;
        }

        // "41-44" stays text in YAML; "41" is a number.
        var lines = bug["lines"] switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            _ => null,
        };
        var match = lines is null ? null : Lines().Match(lines);
        if (match is not { Success: true })
        {
            error($"'{path}.lines' must be a line or a range such as 41-44.");
            return null;
        }

        var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
        var end = match.Groups["end"].Success ? int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture) : start;
        if (start < 1 || end < start)
        {
            error($"'{path}.lines' must start at 1 or later and not end before it starts.");
            return null;
        }

        return new KnownBug(file.Replace('\\', '/'), start, end, about.Trim());
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex Sha();

    [GeneratedRegex(@"^\s*(?<start>\d+)\s*(?:-\s*(?<end>\d+)\s*)?$")]
    private static partial Regex Lines();
}
