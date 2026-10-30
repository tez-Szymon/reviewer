namespace Reviewer.Git;

/// <summary>Git operations the engine needs. Implemented by shelling out to the git CLI.</summary>
public interface IGitRepository
{
    /// <summary>Fetches <paramref name="refspecs"/> (e.g. <c>refs/pull/7/head:refs/reviewer/pr/7/head</c>) without tags.</summary>
    /// <param name="credential">For HTTPS remotes that need auth; null uses no credentials at all.</param>
    Task FetchAsync(string remoteUrl, IReadOnlyList<string> refspecs, GitCredential? credential, CancellationToken ct);

    /// <summary>Checks out <paramref name="sha"/> as a detached worktree at <paramref name="path"/>.</summary>
    Task AddWorktreeAsync(string sha, string path, CancellationToken ct);

    Task RemoveWorktreeAsync(string path, CancellationToken ct);

    Task<string> MergeBaseAsync(string sha1, string sha2, CancellationToken ct);

    /// <param name="paths">Limit the diff to these paths (in <paramref name="toSha"/>); null means all.</param>
    Task<string> DiffAsync(string fromSha, string toSha, CancellationToken ct, IReadOnlyCollection<string>? paths = null);

    /// <summary>Files present in <paramref name="toSha"/> that differ from <paramref name="fromSha"/>, with their changed lines.</summary>
    /// <param name="paths">Limit the result to these paths (in <paramref name="toSha"/>); null means all.</param>
    Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string fromSha, string toSha, CancellationToken ct, IReadOnlyCollection<string>? paths = null);

    /// <summary>
    /// Lines of <paramref name="fromSha"/> that changed on the way to <paramref name="toSha"/>, per path in
    /// <paramref name="fromSha"/>: where something written against the older revision no longer applies.
    /// </summary>
    /// <remarks>
    /// A pure insertion after line n is the empty span (n+1, n), which overlaps only ranges that contain both n and
    /// n+1. A deleted file is one span covering every line.
    /// </remarks>
    Task<IReadOnlyDictionary<string, IReadOnlyList<LineSpan>>> ChangedOldLinesAsync(string fromSha, string toSha, CancellationToken ct);

    /// <summary>True when <paramref name="sha"/> names a commit present in this repository.</summary>
    Task<bool> HasCommitAsync(string sha, CancellationToken ct);

    /// <summary>Points the local ref <paramref name="name"/> at <paramref name="sha"/>, keeping the commit from being pruned.</summary>
    Task UpdateRefAsync(string name, string sha, CancellationToken ct);

    /// <summary>Commits in <paramref name="range"/>, oldest first, merges included.</summary>
    Task<IReadOnlyList<string>> CommitsAsync(string range, CancellationToken ct);

    /// <summary>Content of <paramref name="path"/> at <paramref name="sha"/>, or null when the file doesn't exist there.</summary>
    Task<string?> ReadFileAsync(string sha, string path, CancellationToken ct);

    Task<bool> IsAncestorAsync(string ancestorSha, string descendantSha, CancellationToken ct);

    /// <summary>Stable patch-id per commit SHA in <paramref name="range"/>, used to match commits across rebases.</summary>
    Task<IReadOnlyDictionary<string, string>> PatchIdsAsync(string range, CancellationToken ct);
}

/// <summary>An inclusive range of line numbers in the newer revision.</summary>
public readonly record struct LineSpan(int Start, int End)
{
    public bool Overlaps(int start, int end) => start <= End && end >= Start;
}

/// <param name="Path">Path in the newer revision.</param>
/// <param name="Lines">
/// Changed lines in the newer revision. A deletion is anchored to the line after it. Empty when the file changed
/// without line changes (rename, mode change, binary).
/// </param>
public sealed record ChangedFile(string Path, IReadOnlyList<LineSpan> Lines);

/// <summary>HTTPS credentials for one fetch, e.g. <c>x-access-token</c> and a GitHub token.</summary>
public sealed record GitCredential(string Username, string Password)
{
    // Keep the secret out of logs and exception messages.
    public override string ToString() => $"GitCredential {{ Username = {Username} }}";
}

public sealed class GitException(string message) : Exception(message);

/// <summary>Per-file hashes of a patch, for telling whether a file's change survived a rebase unchanged.</summary>
public static class FilePatchIds
{
    /// <summary>
    /// A hash per file of what <paramref name="patch"/> (full <c>git diff</c> output) changes in it, ignoring line
    /// numbers and whitespace. Keyed by new path, or old path for deletions.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Compute(string patch) => DiffParser.FilePatchIds(patch);
}
