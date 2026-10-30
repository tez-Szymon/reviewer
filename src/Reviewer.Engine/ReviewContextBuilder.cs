using System.Globalization;
using System.Text;
using System.Text.Json;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Git;
using Reviewer.Instructions;

namespace Reviewer.Engine;

/// <summary>Where to fetch a PR from. Provider-specific, e.g. <c>refs/pull/7/head</c> on GitHub.</summary>
/// <param name="Credential">Used for this fetch only; never written to the job folder or the agent's environment.</param>
public sealed record ReviewSource(string RemoteUrl, string HeadRef, GitCredential? Credential = null);

/// <summary>Instruction layers for one review; repo agent files are read from the base branch by the builder.</summary>
public sealed record InstructionSettings(
    bool UseDefaultGuide,
    IReadOnlyList<Skill> UserSkills,
    IReadOnlyList<Skill> RepoSkills,
    RepoAgentFileMode AgentFileMode);

/// <summary>
/// A job ready for the agent: worktree at the PR head and a fresh attempt folder with the input files. Disposing it
/// removes the worktree; the job folder stays for history.
/// </summary>
public sealed class PreparedReview(IGitRepository git, string jobId, string jobFolder, ReviewContext context, ReviewScope scope)
    : IAsyncDisposable
{
    public string JobId { get; } = jobId;

    public string JobFolder { get; } = jobFolder;

    public ReviewContext Context { get; } = context;

    /// <summary>The reviewed head and changed lines, for the policy engine.</summary>
    public ReviewScope Scope { get; } = scope;

    /// <summary>A file at the reviewed head, from git; null when the head has no such file.</summary>
    public Task<string?> ReadHeadFileAsync(string path, CancellationToken ct) => git.ReadFileAsync(Scope.ReviewedHeadSha, path, ct);

    public async ValueTask DisposeAsync() =>
        await ReviewContextBuilder.RemoveWorktreeQuietlyAsync(git, Context.WorktreePath).ConfigureAwait(false);
}

/// <summary>
/// Context builder: fetches the PR, checks out its head, computes the delta and writes the agent's input files.
/// </summary>
/// <remarks>
/// M1 reviews the whole PR: the delta runs from the merge base to the head. Repo agent files come from the base
/// branch only, so the PR can't add instructions for its own review.
/// </remarks>
public sealed class ReviewContextBuilder(IGitRepository git, ReviewerPaths paths, TimeProvider time)
{
    public const string ContextFile = "context.json";
    public const string DeltaFile = "delta.patch";
    public const string InstructionsFile = "instructions.md";
    public const string SchemaFile = "schema.json";

    /// <summary>Repo agent files read from the base branch, in this order.</summary>
    public static readonly IReadOnlyList<string> AgentFileNames = ["CLAUDE.md", "AGENTS.md"];

    // Agent files and descriptions are meant to be short; a huge one would crowd out the delta.
    private const int MaxAgentFileChars = 64 * 1024;
    private const int MaxDescriptionChars = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <param name="tokenOwner">Login of the account that will publish the review, for the self-PR rule.</param>
    /// <exception cref="GitException">
    /// Fetching failed, or the snapshot's head is no longer reachable (force-pushed since the snapshot was taken).
    /// </exception>
    /// <param name="jobId">From <see cref="NewJobId"/>; names the job folder.</param>
    public async Task<PreparedReview> PrepareAsync(
        string jobId,
        PullRequestSnapshot snapshot,
        ReviewSource source,
        string tokenOwner,
        InstructionSettings instructions,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var pr = snapshot.Ref;
        var prefix = $"refs/reviewer/pr/{pr.Number}";
        await git.FetchAsync(
            source.RemoteUrl,
            [$"{source.HeadRef}:{prefix}/head", $"refs/heads/{snapshot.BaseBranch}:{prefix}/base"],
            source.Credential,
            ct).ConfigureAwait(false);

        var jobFolder = Path.Combine(paths.Jobs, jobId);
        var attempt = CreateAttempt(jobFolder);
        // One worktree per attempt, so two runs that land in the same job folder can't collide.
        var worktree = Path.Combine(jobFolder, $"worktree-{Path.GetFileName(attempt)}");
        await git.AddWorktreeAsync(snapshot.HeadSha, worktree, ct).ConfigureAwait(false);

        try
        {
            var mergeBase = await git.MergeBaseAsync(snapshot.BaseSha, snapshot.HeadSha, ct).ConfigureAwait(false);
            var patch = await git.DiffAsync(mergeBase, snapshot.HeadSha, ct).ConfigureAwait(false);
            var changed = await git.ChangedFilesAsync(mergeBase, snapshot.HeadSha, ct).ConfigureAwait(false);
            var agentFiles = instructions.AgentFileMode == RepoAgentFileMode.Ignore
                ? []
                : await ReadAgentFilesAsync(snapshot.BaseSha, ct).ConfigureAwait(false);

            var sources = new InstructionSources(
                instructions.UseDefaultGuide, instructions.UserSkills, instructions.RepoSkills, agentFiles, instructions.AgentFileMode);
            var context = new ContextJson(
                pr.Repo.ToString(),
                pr.Repo.Host.ToString(),
                pr.Number,
                snapshot.Title,
                Truncate(snapshot.Description, MaxDescriptionChars),
                snapshot.Author,
                snapshot.BaseBranch,
                snapshot.BaseSha,
                snapshot.HeadSha,
                new DeltaJson(mergeBase, snapshot.HeadSha),
                snapshot.IsDraft,
                snapshot.Labels,
                [.. changed.Select(f => new ChangedFileJson(f.Path, [.. f.Lines.Select(l => new LineSpanJson(l.Start, l.End))]))]);

            await Write(attempt, ContextFile, JsonSerializer.Serialize(context, JsonOptions), ct).ConfigureAwait(false);
            await Write(attempt, DeltaFile, patch, ct).ConfigureAwait(false);
            await Write(attempt, InstructionsFile, InstructionAssembler.Assemble(sources, [.. changed.Select(f => f.Path)]), ct)
                .ConfigureAwait(false);
            await Write(attempt, SchemaFile, FindingsSchema.Json, ct).ConfigureAwait(false);

            var scope = new ReviewScope(
                snapshot.HeadSha,
                tokenOwner,
                changed.ToDictionary(
                    f => f.Path,
                    IReadOnlyList<LineRange> (f) => [.. f.Lines.Select(l => new LineRange(l.Start, l.End))],
                    StringComparer.Ordinal));
            return new PreparedReview(git, jobId, jobFolder, new ReviewContext(worktree, attempt, timeout), scope);
        }
        catch
        {
            await RemoveWorktreeQuietlyAsync(git, worktree).ConfigureAwait(false);
            throw;
        }
    }

    // Best effort: a leftover worktree is pruned later and must not hide the review's own outcome.
    internal static async Task RemoveWorktreeQuietlyAsync(IGitRepository git, string worktree)
    {
        try
        {
            await git.RemoveWorktreeAsync(worktree, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GitException)
        {
        }
    }

    /// <summary>A readable, sortable id such as <c>20261030-120000-acme-app-7</c>, safe as a folder name.</summary>
    public string NewJobId(PullRequestRef pr)
    {
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"{stamp}-{pr.Repo.Owner}-{pr.Repo.Name}-{pr.Number}";
        return string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
    }

    // A fresh numbered folder per attempt, so a stale result from an earlier attempt can never be read.
    private static string CreateAttempt(string jobFolder)
    {
        Directory.CreateDirectory(jobFolder);
        var next = Directory.EnumerateDirectories(jobFolder)
            .Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return Directory.CreateDirectory(Path.Combine(jobFolder, next.ToString(CultureInfo.InvariantCulture))).FullName;
    }

    private async Task<IReadOnlyList<RepoAgentFile>> ReadAgentFilesAsync(string baseSha, CancellationToken ct)
    {
        var files = new List<RepoAgentFile>();
        foreach (var name in AgentFileNames)
        {
            if (await git.ReadFileAsync(baseSha, name, ct).ConfigureAwait(false) is { } content)
            {
                files.Add(new RepoAgentFile(name, Truncate(content, MaxAgentFileChars)));
            }
        }

        return files;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    private static Task Write(string folder, string name, string content, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(folder, name), content, new UTF8Encoding(false), ct);

    private sealed record ContextJson(
        string Repository,
        string Host,
        int Number,
        string Title,
        string Description,
        string Author,
        string BaseBranch,
        string BaseSha,
        string HeadSha,
        DeltaJson Delta,
        bool IsDraft,
        IReadOnlyList<string> Labels,
        IReadOnlyList<ChangedFileJson> ChangedFiles);

    private sealed record DeltaJson(string From, string To);

    private sealed record ChangedFileJson(string Path, IReadOnlyList<LineSpanJson> Lines);

    private sealed record LineSpanJson(int Start, int End);
}
