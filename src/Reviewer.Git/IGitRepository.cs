namespace Reviewer.Git;

/// <summary>Git operations the engine needs. Implemented by shelling out to the git CLI.</summary>
public interface IGitRepository
{
    Task FetchAsync(string remoteUrl, IReadOnlyList<string> refs, CancellationToken ct);
    Task<string> AddWorktreeAsync(string sha, string path, CancellationToken ct);
    Task RemoveWorktreeAsync(string path, CancellationToken ct);
    Task<string> DiffAsync(string fromSha, string toSha, CancellationToken ct);
    Task<bool> IsAncestorAsync(string ancestorSha, string descendantSha, CancellationToken ct);

    /// <summary>Stable patch-id per commit in <paramref name="range"/>, used to match commits across rebases.</summary>
    Task<IReadOnlyDictionary<string, string>> PatchIdsAsync(string range, CancellationToken ct);
}
