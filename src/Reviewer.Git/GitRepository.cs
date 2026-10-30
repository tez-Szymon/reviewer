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

    public async Task FetchAsync(string remoteUrl, IReadOnlyList<string> refspecs, GitCredential? credential, CancellationToken ct)
    {
        IReadOnlyList<string> args = ["fetch", "--quiet", "--no-tags", "--force", "--", remoteUrl, .. refspecs];
        var result = await RunRawAsync(args, null, ct, CredentialEnvironment(remoteUrl, credential)).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw Failure(args, result);
        }
    }

    /// <summary>
    /// Git config passed through the environment. Nothing of the user's may answer for the engine: every configured
    /// credential helper is dropped and askpass programs are switched off. With a credential, one helper is added that
    /// reads it from environment variables, scoped to the remote's origin so a redirect to another host never gets
    /// it. The secret never appears in arguments, where other processes could see it.
    /// </summary>
    internal static Dictionary<string, string?> CredentialEnvironment(string remoteUrl, GitCredential? credential)
    {
        // An empty GIT_ASKPASS also stops git from falling back to core.askPass and SSH_ASKPASS; Windows can't hold
        // an empty variable, so core.askPass is cleared as well.
        var env = new Dictionary<string, string?> { ["GIT_ASKPASS"] = "" };
        List<(string Key, string Value)> config = [("credential.helper", ""), ("core.askPass", "")];
        if (credential is not null
            && Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remote)
            && remote.Scheme is "https" or "http")
        {
            config.Add((
                $"credential.{remote.GetLeftPart(UriPartial.Authority)}.helper",
                "!f() { test \"$1\" = get && printf 'username=%s\\npassword=%s\\n' \"$REVIEWER_GIT_USERNAME\" \"$REVIEWER_GIT_PASSWORD\"; }; f"));
            env["REVIEWER_GIT_USERNAME"] = credential.Username;
            env["REVIEWER_GIT_PASSWORD"] = credential.Password;
        }

        env["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        return env;
    }

    public Task AddWorktreeAsync(string sha, string path, CancellationToken ct) =>
        RunAsync(["worktree", "add", "--quiet", "--detach", "--", path, sha], ct);

    public async Task RemoveWorktreeAsync(string path, CancellationToken ct)
    {
        await RunAsync(["worktree", "remove", "--force", "--", path], ct).ConfigureAwait(false);
        await RunAsync(["worktree", "prune"], ct).ConfigureAwait(false);
    }

    public async Task<string> MergeBaseAsync(string sha1, string sha2, CancellationToken ct) =>
        (await RunAsync(["merge-base", sha1, sha2], ct).ConfigureAwait(false)).Trim();

    public Task<string> DiffAsync(string fromSha, string toSha, CancellationToken ct, IReadOnlyCollection<string>? paths = null) =>
        RunAsync(["diff", "--no-color", "--no-ext-diff", "--find-renames", fromSha, toSha, "--", .. Pathspecs(paths)], ct);

    public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(
        string fromSha, string toSha, CancellationToken ct, IReadOnlyCollection<string>? paths = null)
    {
        // Renames are detected across the whole diff and then filtered, so a path limit can't turn a rename into an add.
        var nameStatus = await RunAsync(
            ["diff", "--name-status", "-z", "--find-renames", "--no-ext-diff", fromSha, toSha, "--"], ct).ConfigureAwait(false);
        var patch = await RunAsync(
            ["-c", "core.quotePath=false", "diff", "--unified=0", "--no-color", "--no-ext-diff", "--find-renames", fromSha, toSha, "--"],
            ct).ConfigureAwait(false);

        var lines = DiffParser.ParseChangedLines(patch);
        return [.. DiffParser.ParseNameStatus(nameStatus)
            .Where(p => paths is null || paths.Contains(p))
            .Select(p => new ChangedFile(p, lines.TryGetValue(p, out var spans) ? spans : []))];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LineSpan>>> ChangedOldLinesAsync(string fromSha, string toSha, CancellationToken ct)
    {
        var patch = await RunAsync(
            ["-c", "core.quotePath=false", "diff", "--unified=0", "--no-color", "--no-ext-diff", "--find-renames", fromSha, toSha, "--"],
            ct).ConfigureAwait(false);
        return DiffParser.ParseOldChangedLines(patch);
    }

    public async Task<bool> HasCommitAsync(string sha, CancellationToken ct) =>
        (await RunRawAsync(["cat-file", "-e", $"{sha}^{{commit}}"], null, ct).ConfigureAwait(false)).ExitCode == 0;

    public Task UpdateRefAsync(string name, string sha, CancellationToken ct) => RunAsync(["update-ref", name, sha], ct);

    public async Task<IReadOnlyList<string>> CommitsAsync(string range, CancellationToken ct) =>
        (await RunAsync(["rev-list", "--reverse", range, "--"], ct).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // Literal paths: a file named "*.cs" or ":(glob)x" must not be read as a pattern.
    private static IEnumerable<string> Pathspecs(IReadOnlyCollection<string>? paths) =>
        paths?.Select(p => ":(literal)" + p) ?? [];

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

    private Task<BufferedCommandResult> RunRawAsync(
        IReadOnlyList<string> args, string? stdin, CancellationToken ct, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var command = Cli.Wrap("git")
            .WithArguments(["--git-dir", Path, .. args])
            .WithEnvironmentVariables(env =>
            {
                env.Set(Environment);
                if (extraEnvironment is not null)
                {
                    env.Set(extraEnvironment);
                }
            })
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
