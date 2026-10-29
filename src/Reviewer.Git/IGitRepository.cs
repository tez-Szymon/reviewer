namespace Reviewer.Git;

/// <summary>Git operations the engine needs. Implemented by shelling out to the git CLI.</summary>
public interface IGitRepository
{
    /// <summary>Fetches <paramref name="refspecs"/> (e.g. <c>refs/pull/7/head:refs/reviewer/pr/7/head</c>) without tags.</summary>
    Task FetchAsync(string remoteUrl, IReadOnlyList<string> refspecs, CancellationToken ct);

    /// <summary>Checks out <paramref name="sha"/> as a detached worktree at <paramref name="path"/>.</summary>
    Task AddWorktreeAsync(string sha, string path, CancellationToken ct);

    Task RemoveWorktreeAsync(string path, CancellationToken ct);

    Task<string> MergeBaseAsync(string sha1, string sha2, CancellationToken ct);

    Task<string> DiffAsync(string fromSha, string toSha, CancellationToken ct);

    /// <summary>Files present in <paramref name="toSha"/> that differ from <paramref name="fromSha"/>, with their changed lines.</summary>
    Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string fromSha, string toSha, CancellationToken ct);

    Task<bool> IsAncestorAsync(string ancestorSha, string descendantSha, CancellationToken ct);

    /// <summary>Stable patch-id per commit SHA in <paramref name="range"/>, used to match commits across rebases.</summary>
    Task<IReadOnlyDictionary<string, string>> PatchIdsAsync(string range, CancellationToken ct);
}

/// <summary>An inclusive range of line numbers in the newer revision.</summary>
public readonly record struct LineSpan(int Start, int End);

/// <param name="Path">Path in the newer revision.</param>
/// <param name="Lines">
/// Changed lines in the newer revision. A deletion is anchored to the line after it. Empty when the file changed
/// without line changes (rename, mode change, binary).
/// </param>
public sealed record ChangedFile(string Path, IReadOnlyList<LineSpan> Lines);

public sealed class GitException(string message) : Exception(message);
