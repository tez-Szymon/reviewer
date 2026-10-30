using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace Reviewer.Git;

/// <summary>
/// A bare repository the engine owns, one per remote repo, used as the source of per-job worktrees.
/// </summary>
public sealed class GitRepository : IGitRepository
{
    // Never prompt for credentials, and keep output stable regardless of the user's settings.
    private static readonly Dictionary<string, string?> Environment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_PAGER"] = "cat",
        ["LC_ALL"] = "C",
    };

    private GitRepository(string path) => Path = path;

    public string Path { get; }

    /// <summary>Creates the bare repository if needed. Safe to call on an existing one.</summary>
    public static async Task<GitRepository> OpenOrInitAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        var repo = new GitRepository(path);
        await repo.RunAsync(["init", "--bare", "--quiet"], ct).ConfigureAwait(false);

        // Worktrees check out untrusted PR content: make sure no hook (ours or the user's global ones) runs.
        var noHooks = System.IO.Path.Combine(path, "reviewer-no-hooks");
        Directory.CreateDirectory(noHooks);
        await repo.RunAsync(["config", "core.hooksPath", noHooks], ct).ConfigureAwait(false);
        return repo;
    }

    public Task FetchAsync(string remoteUrl, IReadOnlyList<string> refspecs, CancellationToken ct) =>
        RunAsync(["fetch", "--quiet", "--no-tags", "--force", "--", remoteUrl, .. refspecs], ct);

    public Task AddWorktreeAsync(string sha, string path, CancellationToken ct) =>
        RunAsync(["worktree", "add", "--quiet", "--detach", "--", path, sha], ct);

    public async Task RemoveWorktreeAsync(string path, CancellationToken ct)
    {
        await RunAsync(["worktree", "remove", "--force", "--", path], ct).ConfigureAwait(false);
        await RunAsync(["worktree", "prune"], ct).ConfigureAwait(false);
    }

    public async Task<string> MergeBaseAsync(string sha1, string sha2, CancellationToken ct) =>
        (await RunAsync(["merge-base", sha1, sha2], ct).ConfigureAwait(false)).Trim();

    public Task<string> DiffAsync(string fromSha, string toSha, CancellationToken ct) =>
        RunAsync(["diff", "--no-color", "--no-ext-diff", "--find-renames", fromSha, toSha, "--"], ct);

    public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string fromSha, string toSha, CancellationToken ct)
    {
        var nameStatus = await RunAsync(
            ["diff", "--name-status", "-z", "--find-renames", "--no-ext-diff", fromSha, toSha, "--"], ct).ConfigureAwait(false);
        var patch = await RunAsync(
            ["-c", "core.quotePath=false", "diff", "--unified=0", "--no-color", "--no-ext-diff", "--find-renames", fromSha, toSha, "--"],
            ct).ConfigureAwait(false);

        var lines = DiffParser.ParseChangedLines(patch);
        return [.. DiffParser.ParseNameStatus(nameStatus)
            .Select(p => new ChangedFile(p, lines.TryGetValue(p, out var spans) ? spans : []))];
    }

    public async Task<string?> ReadFileAsync(string sha, string path, CancellationToken ct)
    {
        // "-e" exits non-zero for a missing path; the commit itself is expected to be present.
        var spec = $"{sha}:{path}";
        var exists = await RunRawAsync(["cat-file", "-e", spec], null, ct).ConfigureAwait(false);
        return exists.ExitCode == 0
            ? await RunAsync(["cat-file", "blob", spec], ct).ConfigureAwait(false)
            : null;
    }

    public async Task<bool> IsAncestorAsync(string ancestorSha, string descendantSha, CancellationToken ct)
    {
        var result = await RunRawAsync(["merge-base", "--is-ancestor", ancestorSha, descendantSha], null, ct).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw Failure(["merge-base", "--is-ancestor"], result),
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> PatchIdsAsync(string range, CancellationToken ct)
    {
        var log = await RunAsync(["log", "--no-merges", "--no-color", "--no-ext-diff", "--patch", "--format=commit %H", range, "--"], ct)
            .ConfigureAwait(false);
        var ids = await RunAsync(["patch-id", "--stable"], ct, stdin: log).ConfigureAwait(false);

        // Output lines are "<patch-id> <commit>"; commits with an empty diff produce none.
        return ids.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .ToDictionary(parts => parts[1], parts => parts[0]);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct, string? stdin = null)
    {
        var result = await RunRawAsync(args, stdin, ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput : throw Failure(args, result);
    }

    private Task<BufferedCommandResult> RunRawAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var command = Cli.Wrap("git")
            .WithArguments(["--git-dir", Path, .. args])
            .WithEnvironmentVariables(Environment)
            .WithValidation(CommandResultValidation.None);
        if (stdin is not null)
        {
            command = command.WithStandardInputPipe(PipeSource.FromString(stdin, Encoding.UTF8));
        }

        return command.ExecuteBufferedAsync(Encoding.UTF8, ct);
    }

    // Arguments are included but never the remote URL's credentials: callers pass tokens via a credential helper.
    private static GitException Failure(IReadOnlyList<string> args, BufferedCommandResult result)
    {
        var subcommand = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || args[i - 1] != "-c")).FirstOrDefault();
        return new($"git {subcommand} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
    }
}
